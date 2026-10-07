using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

public sealed class JanitorOptions
{
    public List<FolderRule> Folders { get; set; } = [];
    public int RescanMinutes { get; set; } = 5;
    public bool DryRun { get; set; } = false;            // true = simulação em todas as pastas
}

public enum Verdict { Match, Short, NoText, DuplicateNf }

public sealed class FolderRule
{
    public string Path { get; set; } = "";
    public string Action { get; set; } = "Delete";       // Move | Delete
    public string MoveTo { get; set; } = "";
    public int MaxNameLength { get; set; } = 32;
    public bool CountExtension { get; set; } = true;     // "relatorio.pdf" conta 13 ou 9?
    public List<string> NameContains { get; set; } = []; // vazio = qualquer nome; senão precisa conter um deles
    public bool IncludeSubdirectories { get; set; } = false;
    public bool DryRun { get; set; } = false;            // simulação: só mostra o que seria apagado/movido

    // NF repetida: entre os arquivos que contêm NameContains, guarda a NF do primeiro que fica na pasta e
    // trata (apaga/move) qualquer outro arquivo com a mesma NF. O grupo 1 do padrão é o número da NF.
    public bool DeleteDuplicateNf { get; set; } = false;
    public string NfPattern { get; set; } = @"^NF0*(\d+)_";

    Regex? nfRegex;
    public Regex NfRegex => nfRegex ??= new Regex(NfPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public bool IsMove => Action.Equals("Move", StringComparison.OrdinalIgnoreCase);
}

public sealed class Janitor(IOptions<JanitorOptions> opt, Stats stats, NfCache nfCache, ILogger<Janitor> log) : BackgroundService
{
    static readonly TimeSpan MinRetry = TimeSpan.FromSeconds(5), MaxRetry = TimeSpan.FromMinutes(5);

    readonly JanitorOptions o = opt.Value;
    readonly Channel<(string Path, FolderRule Rule, string Reason, bool Duplicate)> queue =
        Channel.CreateUnbounded<(string, FolderRule, string, bool)>(new() { SingleReader = true });

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (o.Folders.Count == 0)
        {
            log.LogError("Nenhuma pasta em Janitor:Folders no appsettings.json");
            return;
        }
        foreach (var r in o.Folders)
        {
            if (o.DryRun) r.DryRun = true;
            if (r.DryRun) log.LogWarning("MODO SIMULAÇÃO em {Path}: nada será apagado nem movido", r.Path);
            if (r.IsMove && string.IsNullOrWhiteSpace(r.MoveTo))
                throw new InvalidOperationException($"{r.Path}: Action=Move precisa de MoveTo");
            stats.Register(r);
            _ = Supervise(r, ct);
        }
        await foreach (var (path, rule, reason, duplicate) in queue.Reader.ReadAllAsync(ct))
        {
            stats.Dequeued();
            await Handle(path, rule, reason, duplicate, ct);
        }
    }

    // Mantém o watcher de uma pasta vivo. Em compartilhamento de rede a conexão cai (reinício do servidor,
    // rede, SMB): o watcher morre com Error, ou a varredura falha. Nos dois casos recria tudo, com espera crescente.
    async Task Supervise(FolderRule r, CancellationToken ct)
    {
        var retry = MinRetry;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Directory.Exists(r.Path)) throw new DirectoryNotFoundException($"Pasta inacessível: {r.Path}");
                if (r.IsMove) Directory.CreateDirectory(r.MoveTo);

                var broken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var w = new FileSystemWatcher(r.Path)
                {
                    IncludeSubdirectories = r.IncludeSubdirectories,
                    NotifyFilter = NotifyFilters.FileName,
                    InternalBufferSize = 64 * 1024, // máximo aceito em pasta de rede
                };
                w.Created += (_, e) => Enqueue(e.FullPath, r);
                w.Renamed += (_, e) => Enqueue(e.FullPath, r);
                w.Error += (_, e) => broken.TrySetException(e.GetException());
                w.EnableRaisingEvents = true;

                log.LogInformation("Vigiando {Path} ({Action})", r.Path, r.Action);
                stats.SetState(r, FolderState.Watching);
                Scan(r);
                retry = MinRetry;

                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(o.RescanMinutes));
                while (true)
                {
                    var tick = timer.WaitForNextTickAsync(ct).AsTask();
                    if (await Task.WhenAny(tick, broken.Task) == broken.Task) await broken.Task; // lança o erro
                    if (!await tick) return;
                    Scan(r);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "{Path}: perdeu a conexão, tentando de novo em {Sec}s", r.Path, retry.TotalSeconds);
                stats.SetState(r, FolderState.Reconnecting, ex.Message, DateTimeOffset.Now + retry);
                try { await Task.Delay(retry, ct); } catch (OperationCanceledException) { return; }
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
            }
        }
    }

    void Scan(FolderRule r)
    {
        var eo = new EnumerationOptions { RecurseSubdirectories = r.IncludeSubdirectories, IgnoreInaccessible = true, AttributesToSkip = 0 };
        // Do mais antigo para o mais novo: com NF repetida, fica o primeiro documento que chegou.
        var files = new DirectoryInfo(r.Path).EnumerateFiles("*", eo).OrderBy(f => f.CreationTimeUtc).Select(f => f.FullName).ToList();
        foreach (var f in files) Enqueue(f, r);
        stats.Scanned(r);
    }

    void Enqueue(string path, FolderRule r)
    {
        if (r.IsMove && path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.MoveTo)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) return;
        var name = Path.GetFileName(path);
        var verdict = Classify(name, r);
        var reason = $"nome com {r.MaxNameLength + 1}+ caracteres";

        // Ficaria na pasta pelo tamanho: confere se é outro documento para uma NF já vista.
        if (verdict == Verdict.Short && r.DeleteDuplicateNf && HasText(name, r) && r.NfRegex.Match(name) is { Success: true } m)
        {
            var nf = m.Groups[1].Value.TrimStart('0') is { Length: > 0 } n ? n : "0";
            if (nfCache.Register(nf, name) is { } first)
            {
                verdict = Verdict.DuplicateNf;
                reason = $"NF {nf} repetida (primeiro: {first})";
            }
        }

        stats.Checked(r, verdict);
        if (verdict is not (Verdict.Match or Verdict.DuplicateNf)) return;
        var duplicate = verdict == Verdict.DuplicateNf;
        if (r.DryRun)
        {
            if (stats.Simulated(r, path, reason, duplicate))
                log.LogInformation("Simulação, seria {Action} ({Reason}): {Src}", r.IsMove ? "movido" : "apagado", reason, path);
            return;
        }
        stats.Queued();
        queue.Writer.TryWrite((path, r, reason, duplicate));
    }

    static bool HasText(string name, FolderRule r) =>
        r.NameContains.Count == 0 || r.NameContains.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));

    static Verdict Classify(string name, FolderRule r)
    {
        var counted = r.CountExtension ? name : Path.GetFileNameWithoutExtension(name);
        if (counted.Length <= r.MaxNameLength) return Verdict.Short;
        return HasText(name, r) ? Verdict.Match : Verdict.NoText;
    }

    async Task Handle(string path, FolderRule r, string reason, bool duplicate, CancellationToken ct)
    {
        // Arquivo ainda sendo copiado fica travado: tenta por ~10 s; se não der, a próxima varredura pega.
        for (int i = 0; i < 20; i++)
        {
            try
            {
                if (!File.Exists(path)) return;
                if (r.IsMove)
                {
                    var dest = UniqueDest(r.MoveTo, Path.GetFileName(path));
                    File.Move(path, dest);
                    log.LogInformation("Movido ({Reason}): {Src} -> {Dest}", reason, path, dest);
                    stats.Done(r, ActivityKind.Moved, path, dest, reason, duplicate);
                }
                else
                {
                    File.Delete(path);
                    log.LogInformation("Apagado ({Reason}): {Src}", reason, path);
                    stats.Done(r, ActivityKind.Deleted, path, null, reason, duplicate);
                }
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                log.LogError(ex, "Sem permissão: {Src}", path);
                stats.Failed(r, path, "Sem permissão");
                return;
            }
            catch (IOException) { await Task.Delay(500, ct); }
        }
        log.LogWarning("Ainda travado, fica para a próxima varredura: {Src}", path);
        stats.Failed(r, path, "Arquivo em uso; tenta de novo na próxima varredura");
    }

    static string UniqueDest(string dir, string name)
    {
        var dest = Path.Combine(dir, name);
        if (!File.Exists(dest)) return dest;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
        return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}_{stamp}{Path.GetExtension(name)}");
    }
}
