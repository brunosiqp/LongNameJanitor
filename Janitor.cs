using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

public sealed class JanitorOptions
{
    public List<FolderRule> Folders { get; set; } = [];
    public int RescanMinutes { get; set; } = 5;
    public bool DryRun { get; set; } = false;            // true = simulação em todas as pastas
    public int NfCacheDays { get; set; } = 30;
    public int HistoryZipAfterDays { get; set; } = 7;    // CSV do histórico parado há N dias vira .zip em historico\arquivados
    public int HistoryKeepZipDays { get; set; } = 365;   // .zip mais velho que isso é apagado
}

public enum DuplicateMode { Off, Warn, Delete }

public sealed class FolderRule
{
    public string Path { get; set; } = "";
    public string Action { get; set; } = "Delete";       // Move | Delete
    public string MoveTo { get; set; } = "";
    public int MaxNameLength { get; set; } = 32;
    public bool CountExtension { get; set; } = true;     // "relatorio.pdf" conta 13 ou 9?
    public List<string> NameContains { get; set; } = []; // obrigatório: o nome precisa conter um deles
    public bool IncludeSubdirectories { get; set; } = false;
    public bool DryRun { get; set; } = false;            // simulação: só mostra o que seria feito

    // O que a pasta faz (cada um liga/desliga):
    public bool CleanFolder { get; set; } = true;        // limpar a pasta vigiada
    public bool LongNameRule { get; set; } = true;       // apagar/mover nome longo

    // NF repetida (entre arquivos com NameContains): Off, Warn (só avisa, o arquivo fica) ou Delete (apaga/move).
    // O grupo 1 do NfPattern é o número da NF; zeros à esquerda são ignorados. O cache é por pasta.
    public string DuplicateNf { get; set; } = "";
    public bool? DeleteDuplicateNf { get; set; }         // v1.2: true = Delete (use DuplicateNf)
    public string NfPattern { get; set; } = @"^NF0*(\d+)_";

    // Pasta de processados: arquivos com NameContains em ProcessedPath\AAAA\MM\DD (hoje e, se quiser, ontem)
    // vão para ProcessedPath\LongNameJanitor, substituindo o de mesmo nome.
    public string ProcessedPath { get; set; } = "";
    public bool ProcessedAuto { get; set; } = true;
    public bool ProcessedYesterday { get; set; } = true;

    public const string ProcessedTargetName = "LongNameJanitor";

    public bool IsMove => Action.Equals("Move", StringComparison.OrdinalIgnoreCase);
    public DuplicateMode DuplicateMode =>
        Enum.TryParse<DuplicateMode>(DuplicateNf, true, out var m) ? m : DeleteDuplicateNf == true ? DuplicateMode.Delete : DuplicateMode.Off;
    public string ProcessedTarget => System.IO.Path.Combine(ProcessedPath, ProcessedTargetName);
    public string Key => System.IO.Path.TrimEndingDirectorySeparator(Path).ToLowerInvariant();

    Regex? nfRegex;
    public Regex NfRegex => nfRegex ??= new Regex(NfPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public bool HasText(string name) => NameContains.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> ProcessedDayFolders(DateTime now)
    {
        if (ProcessedPath == "") yield break;
        yield return DayFolder(now);
        if (ProcessedYesterday) yield return DayFolder(now.AddDays(-1));
    }

    string DayFolder(DateTime d) => System.IO.Path.Combine(ProcessedPath, d.ToString("yyyy"), d.ToString("MM"), d.ToString("dd"));
}

public enum Verdict { Long, LongDuplicate, DuplicateNf, Short, NoText, Processed }
public enum Outcome { Keep, Warn, Act }
public enum WorkKind { Act, ProcessedMove }
public enum Target { Watched, Processed }

public readonly record struct Decision(Verdict Verdict, Outcome Outcome, string Reason);
public readonly record struct Work(string Path, FolderRule Rule, WorkKind Kind, string Reason, bool Duplicate);

public sealed class Janitor(IOptionsMonitor<JanitorOptions> opts, Stats stats, NfCache nfCache, Control control,
    ILogger<Janitor> log) : BackgroundService
{
    static readonly TimeSpan MinRetry = TimeSpan.FromSeconds(5), MaxRetry = TimeSpan.FromMinutes(5);

    readonly Channel<Work> queue = Channel.CreateUnbounded<Work>(new() { SingleReader = true });
    readonly SemaphoreSlim restartGate = new(1, 1);
    CancellationTokenSource? round;
    TaskCompletionSource scanNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int restartVersion;

    JanitorOptions O => opts.CurrentValue;
    bool IsDryRun(FolderRule r) => O.DryRun || r.DryRun;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Configuração (appsettings.local.json editado à mão ou pelo painel) e pausa recriam os vigias.
        using var onChange = opts.OnChange(_ => RestartSoon(ct));
        control.Changed += () => RestartSoon(ct);
        control.ScanRequested += () => Interlocked.Exchange(ref scanNow,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

        await Restart(ct);
        await foreach (var w in queue.Reader.ReadAllAsync(ct))
        {
            stats.Dequeued();
            await Handle(w, ct);
        }
    }

    // A troca do arquivo dispara vários eventos seguidos: espera 1 s e aplica só o último.
    void RestartSoon(CancellationToken ct)
    {
        var v = Interlocked.Increment(ref restartVersion);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000, ct);
            if (v == Volatile.Read(ref restartVersion)) await Restart(ct);
        }, ct);
    }

    async Task Restart(CancellationToken ct)
    {
        await restartGate.WaitAsync(ct);
        try
        {
            round?.Cancel();
            round = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = round.Token;
            var o = O;
            stats.SetFolders(o.Folders);
            stats.ResetSeen();

            if (o.Folders.Count == 0) log.LogWarning("Nenhuma pasta em Janitor:Folders (appsettings.local.json)");
            for (int i = 0; i < o.Folders.Count; i++)
            {
                var r = o.Folders[i];
                if (RuleValidator.Validate(r, i + 1) is { } error)
                {
                    log.LogError("Pasta {N} ignorada: {Error}", i + 1, error);
                    stats.SetState(r, Target.Watched, FolderState.Invalid, error);
                    continue;
                }
                if (control.Paused)
                {
                    stats.SetState(r, Target.Watched, FolderState.Paused);
                    if (r.ProcessedPath != "") stats.SetState(r, Target.Processed, FolderState.Paused);
                    continue;
                }
                if (IsDryRun(r)) log.LogWarning("MODO SIMULAÇÃO em {Path}: nada será apagado nem movido", r.Path);

                if (r.CleanFolder) _ = Supervise(r, Target.Watched, token);
                else stats.SetState(r, Target.Watched, FolderState.Off);

                if (r.ProcessedPath == "") continue;
                if (r.ProcessedAuto) _ = Supervise(r, Target.Processed, token);
                else stats.SetState(r, Target.Processed, FolderState.Off);
            }
            if (control.Paused) log.LogWarning("PAUSADO: nenhuma pasta está sendo vigiada");
        }
        finally { restartGate.Release(); }
    }

    // Mantém o vigia de uma pasta. Em compartilhamento de rede a conexão cai (reinício do servidor, rede, SMB):
    // o watcher morre com Error, ou a varredura falha. Nos dois casos recria tudo, com espera crescente.
    async Task Supervise(FolderRule r, Target target, CancellationToken ct)
    {
        var dir = target == Target.Watched ? r.Path : r.ProcessedPath;
        var retry = MinRetry;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"Pasta inacessível: {dir}");
                if (target == Target.Watched && r.IsMove) Directory.CreateDirectory(r.MoveTo);

                var broken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var w = new FileSystemWatcher(dir)
                {
                    // na de processados o arquivo cai em AAAA\MM\DD: precisa das subpastas
                    IncludeSubdirectories = target == Target.Processed || r.IncludeSubdirectories,
                    NotifyFilter = NotifyFilters.FileName,
                    InternalBufferSize = 64 * 1024, // máximo aceito em pasta de rede
                };
                FileSystemEventHandler onFile = target == Target.Watched
                    ? (_, e) => Inspect(e.FullPath, r)
                    : (_, e) => InspectProcessed(e.FullPath, r, DateTime.Now);
                w.Created += onFile;
                w.Renamed += (s, e) => onFile(s, e);
                w.Error += (_, e) => broken.TrySetException(e.GetException());
                w.EnableRaisingEvents = true;

                log.LogInformation("Vigiando {Path} ({What})", dir, target == Target.Watched ? r.Action : "processados");
                stats.SetState(r, target, FolderState.Watching);
                Sweep(r, target);
                retry = MinRetry;

                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, O.RescanMinutes)));
                Task<bool>? tick = null;
                while (true)
                {
                    tick ??= timer.WaitForNextTickAsync(ct).AsTask();
                    var now = Volatile.Read(ref scanNow).Task;
                    var done = await Task.WhenAny(tick, broken.Task, now);
                    if (done == broken.Task) await broken.Task; // lança o erro
                    if (done == tick)
                    {
                        if (!await tick) return;
                        tick = null;
                    }
                    Sweep(r, target);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "{Path}: sem acesso, tentando de novo em {Sec}s", dir, retry.TotalSeconds);
                stats.SetState(r, target, FolderState.Reconnecting, ex.Message, DateTimeOffset.Now + retry);
                try { await Task.Delay(retry, ct); } catch (OperationCanceledException) { return; }
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
            }
        }
    }

    void Sweep(FolderRule r, Target target)
    {
        if (target == Target.Watched)
        {
            var eo = new EnumerationOptions { RecurseSubdirectories = r.IncludeSubdirectories, IgnoreInaccessible = true, AttributesToSkip = 0 };
            // Do mais antigo para o mais novo: com NF repetida, o primeiro documento que chegou é o que fica.
            var files = new DirectoryInfo(r.Path).EnumerateFiles("*", eo)
                .OrderBy(f => f.CreationTimeUtc).Select(f => f.FullName).ToList();
            foreach (var f in files) Inspect(f, r);
        }
        else
        {
            var now = DateTime.Now;
            foreach (var day in r.ProcessedDayFolders(now))
            {
                List<string> files;
                try { files = Directory.EnumerateFiles(day).Order(StringComparer.OrdinalIgnoreCase).ToList(); }
                catch (DirectoryNotFoundException) { continue; } // pasta do dia ainda não existe
                foreach (var f in files) InspectProcessed(f, r, now);
            }
        }
        stats.Scanned(r, target);
    }

    void Inspect(string path, FolderRule r)
    {
        if (r.IsMove && IsUnder(path, r.MoveTo)) return;
        var d = Decide(System.IO.Path.GetFileName(path), r, nfCache);
        stats.Checked(r, d.Verdict, d.Outcome != Outcome.Act);
        switch (d.Outcome)
        {
            case Outcome.Keep:
                return;
            case Outcome.Warn:
                if (stats.Warned(r, path, d.Reason)) log.LogInformation("Aviso ({Reason}): {Src}", d.Reason, path);
                return;
        }
        var duplicate = d.Verdict == Verdict.DuplicateNf;
        if (IsDryRun(r))
        {
            var detail = r.IsMove ? $"Seria movido para {r.MoveTo}" : "Seria apagado";
            if (stats.Simulated(r, path, d.Reason, duplicate, detail))
                log.LogInformation("Simulação, {Detail} ({Reason}): {Src}", detail.ToLowerInvariant(), d.Reason, path);
            return;
        }
        stats.Queued();
        queue.Writer.TryWrite(new Work(path, r, WorkKind.Act, d.Reason, duplicate));
    }

    void InspectProcessed(string path, FolderRule r, DateTime now)
    {
        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        var day = r.ProcessedDayFolders(now).FirstOrDefault(d => SamePath(d, dir));
        if (day is null) return; // fora da pasta do dia (inclui a própria LongNameJanitor)
        var name = System.IO.Path.GetFileName(path);
        var hasText = r.HasText(name);
        stats.Checked(r, hasText ? Verdict.Processed : Verdict.NoText, !hasText);
        if (!hasText) return;

        var reason = $"processado do dia ({System.IO.Path.GetRelativePath(r.ProcessedPath, day)})";
        if (IsDryRun(r))
        {
            var detail = $"Seria movido para {r.ProcessedTarget} (substitui se já existir)";
            if (stats.Simulated(r, path, reason, false, detail, processed: true))
                log.LogInformation("Simulação, seria movido ({Reason}): {Src}", reason, path);
            return;
        }
        stats.Queued();
        queue.Writer.TryWrite(new Work(path, r, WorkKind.ProcessedMove, reason, false));
    }

    // Separado da varredura para dar para testar sem disco. Olha sempre as duas regras e o motivo diz o que bateu.
    // O cache de NF só guarda arquivo que FICA: um arquivo que sai por nome longo não vira o "primeiro" da NF,
    // senão a cópia boa que chegasse depois seria tratada como repetida e não sobraria nenhuma.
    public static Decision Decide(string name, FolderRule r, NfCache cache)
    {
        if (!r.HasText(name)) return new(Verdict.NoText, Outcome.Keep, "");
        var counted = r.CountExtension ? name : System.IO.Path.GetFileNameWithoutExtension(name);
        var longName = r.LongNameRule && counted.Length > r.MaxNameLength;

        string? nf = null, first = null;
        if (r.DuplicateMode != DuplicateMode.Off && r.NfRegex.Match(name) is { Success: true } m && m.Groups.Count > 1)
        {
            nf = m.Groups[1].Value.TrimStart('0') is { Length: > 0 } n ? n : "0";
            first = cache.Seen(r.Key, nf, name, register: !longName);
        }
        var repeated = first is null ? null : $"NF {nf} repetida (primeiro: {first})";

        if (longName)
            return new(repeated is null ? Verdict.Long : Verdict.LongDuplicate, Outcome.Act,
                $"nome com {r.MaxNameLength + 1}+ caracteres" + (repeated is null ? "" : " e " + repeated));
        if (repeated is not null)
            return new(Verdict.DuplicateNf, r.DuplicateMode == DuplicateMode.Delete ? Outcome.Act : Outcome.Warn, repeated);
        return new(Verdict.Short, Outcome.Keep, "");
    }

    async Task Handle(Work w, CancellationToken ct)
    {
        var r = w.Rule;
        // Arquivo ainda sendo copiado fica travado: tenta por ~10 s; se não der, a próxima varredura pega.
        for (int i = 0; i < 20; i++)
        {
            try
            {
                if (!File.Exists(w.Path)) return;
                if (w.Kind == WorkKind.ProcessedMove)
                {
                    var (dest, replaced) = FileOps.MoveReplacing(w.Path, r.ProcessedTarget);
                    log.LogInformation("Movido ({Reason}): {Src} -> {Dest}{Replaced}", w.Reason, w.Path, dest, replaced ? " (substituído)" : "");
                    stats.Done(r, ActivityKind.Moved, w.Path, dest + (replaced ? " (substituiu o que estava lá)" : ""), w.Reason, false, processed: true);
                }
                else if (r.IsMove)
                {
                    var dest = FileOps.UniqueDest(r.MoveTo, System.IO.Path.GetFileName(w.Path));
                    File.Move(w.Path, dest);
                    log.LogInformation("Movido ({Reason}): {Src} -> {Dest}", w.Reason, w.Path, dest);
                    stats.Done(r, ActivityKind.Moved, w.Path, dest, w.Reason, w.Duplicate);
                }
                else
                {
                    File.Delete(w.Path);
                    log.LogInformation("Apagado ({Reason}): {Src}", w.Reason, w.Path);
                    stats.Done(r, ActivityKind.Deleted, w.Path, null, w.Reason, w.Duplicate);
                }
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                log.LogError(ex, "Sem permissão: {Src}", w.Path);
                stats.Failed(r, w.Path, "Sem permissão");
                return;
            }
            catch (IOException) { await Task.Delay(500, ct); }
        }
        log.LogWarning("Ainda travado, fica para a próxima varredura: {Src}", w.Path);
        stats.Failed(r, w.Path, "Arquivo em uso; tenta de novo na próxima varredura");
    }

    static bool IsUnder(string path, string dir) =>
        path.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dir)) + System.IO.Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a), System.IO.Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}

public static class FileOps
{
    public static string UniqueDest(string dir, string name)
    {
        var dest = Path.Combine(dir, name);
        if (!File.Exists(dest)) return dest;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
        return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}_{stamp}{Path.GetExtension(name)}");
    }

    // Move para a pasta, substituindo o arquivo de mesmo nome (mesmo compartilhamento: troca de uma vez).
    public static (string Dest, bool Replaced) MoveReplacing(string src, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var dest = Path.Combine(targetDir, Path.GetFileName(src));
        var replaced = File.Exists(dest);
        File.Move(src, dest, overwrite: true);
        return (dest, replaced);
    }
}
