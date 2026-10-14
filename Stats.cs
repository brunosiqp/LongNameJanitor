using System.IO.Compression;
using System.Text;
using System.Text.Json;

// Contadores e histórico do painel. Ficam salvos em data\stats.json (sobrevivem a reinícios) e cada arquivo
// tratado vira uma linha em data\historico\aaaa-MM-dd.csv.
public enum FolderState { Connecting, Watching, Reconnecting, Paused, Off, Invalid }
public enum ActivityKind { Deleted, Moved, Error, Simulated, Warning }

public sealed record Activity(DateTimeOffset At, string Kind, string Name, string Folder, string? Detail, string? Reason = null);

public sealed class Stats
{
    const int MaxRecent = 300, MaxDays = 400, MaxSeen = 200_000;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    sealed class TargetInfo
    {
        public FolderState State = FolderState.Connecting;
        public DateTimeOffset Since = DateTimeOffset.Now;
        public DateTimeOffset? LastScan, RetryAt;
        public string? LastError;
    }

    sealed class FolderInfo
    {
        public FolderRule Rule = null!;
        public TargetInfo Watched = new(), Processed = new();
    }

    sealed class Saved
    {
        public DateTimeOffset FirstStart { get; set; } = DateTimeOffset.Now;
        public long Total { get; set; }                                       // removidos (apagados + movidos)
        public long Errors { get; set; }
        public SortedDictionary<string, int> Daily { get; set; } = [];        // "2026-10-02" -> removidos
        public Dictionary<string, long> FolderDone { get; set; } = [];        // pasta -> removidos
        public List<Activity> Recent { get; set; } = [];

        // Verificações: cada vez que um arquivo é olhado (evento ou varredura), com o motivo de ter ficado.
        public DateTimeOffset? CheckedSince { get; set; }                     // versões antigas não contavam
        public long Checked { get; set; }
        public long KeptShort { get; set; }
        public long KeptNoText { get; set; }
        public long Simulated { get; set; }
        public long Duplicates { get; set; }                                  // removidos por NF repetida
        public long Warnings { get; set; }                                    // NF repetida que só avisou (ficou)
        public long Processed { get; set; }                                   // movidos da pasta de processados
        public SortedDictionary<string, int> DuplicatesDaily { get; set; } = [];
        public SortedDictionary<string, int> CheckedDaily { get; set; } = [];
        public SortedDictionary<string, int> KeptShortDaily { get; set; } = [];
        public SortedDictionary<string, int> KeptNoTextDaily { get; set; } = [];
        public SortedDictionary<string, int> SimulatedDaily { get; set; } = [];
        public SortedDictionary<string, int> WarningsDaily { get; set; } = [];
        public SortedDictionary<string, int> ProcessedDaily { get; set; } = [];
        public SortedDictionary<string, int> ErrorsDaily { get; set; } = [];
        public Dictionary<string, long> FolderChecked { get; set; } = [];
    }

    readonly Lock gate = new();
    readonly DateTimeOffset started = DateTimeOffset.Now;
    readonly string dataDir, statePath, historyDir;
    readonly ILogger<Stats> log;
    readonly NfCache nfCache;
    readonly Saved saved;
    readonly Dictionary<string, FolderInfo> folders = [];   // Rule.Key -> estado
    readonly List<string> folderOrder = [];
    readonly int[] perMinute = new int[60];
    readonly long[] minuteStamp = new long[60];
    readonly HashSet<string> simulatedSeen = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> warnedSeen = new(StringComparer.OrdinalIgnoreCase);
    int pending;
    bool dirty;

    public string DataDir => dataDir;

    public Stats(IConfiguration config, NfCache nfCache, ILogger<Stats> log)
    {
        this.log = log;
        this.nfCache = nfCache;
        dataDir = Path.GetFullPath(config["Janitor:DataPath"] ?? "data", AppContext.BaseDirectory);
        statePath = Path.Combine(dataDir, "stats.json");
        historyDir = Path.Combine(dataDir, "historico");
        Directory.CreateDirectory(historyDir);
        saved = Load();
        if (saved.CheckedSince is null) { saved.CheckedSince = DateTimeOffset.Now; dirty = true; }
        foreach (var a in saved.Recent.Where(a => a.Kind is nameof(ActivityKind.Deleted) or nameof(ActivityKind.Moved) or nameof(ActivityKind.Simulated)))
            CountMinute(a.At.ToUnixTimeSeconds() / 60);
    }

    void CountMinute(long m)
    {
        if (MinuteNow() - m >= 60) return;
        var i = (int)(m % 60);
        if (minuteStamp[i] != m) { minuteStamp[i] = m; perMinute[i] = 0; }
        perMinute[i]++;
    }

    Saved Load()
    {
        try
        {
            if (File.Exists(statePath))
                return JsonSerializer.Deserialize<Saved>(File.ReadAllText(statePath)) ?? new();
        }
        catch (Exception ex)
        {
            // Arquivo corrompido não pode derrubar o serviço: guarda uma cópia e recomeça.
            log.LogError(ex, "Não deu para ler {Path}; começando do zero (cópia em .bad)", statePath);
            try { File.Copy(statePath, statePath + ".bad", true); } catch { }
        }
        return new();
    }

    public void Save()
    {
        string text;
        lock (gate)
        {
            if (!dirty) return;
            text = JsonSerializer.Serialize(saved, Json);
            dirty = false;
        }
        try
        {
            var tmp = statePath + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, statePath, true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Não deu para salvar {Path}", statePath);
            lock (gate) dirty = true;
        }
    }

    // ------------------------------------------------------------------------------------------- estado das pastas
    public void SetFolders(IReadOnlyList<FolderRule> rules)
    {
        lock (gate)
        {
            var old = new Dictionary<string, FolderInfo>(folders);
            folders.Clear();
            folderOrder.Clear();
            foreach (var r in rules)
            {
                if (folders.ContainsKey(r.Key)) continue;
                var f = old.GetValueOrDefault(r.Key) ?? new FolderInfo();
                f.Rule = r;
                folders[r.Key] = f;
                folderOrder.Add(r.Key);
            }
        }
    }

    TargetInfo? Info(FolderRule r, Target t) =>
        folders.TryGetValue(r.Key, out var f) ? (t == Target.Watched ? f.Watched : f.Processed) : null;

    public void SetState(FolderRule r, Target t, FolderState state, string? error = null, DateTimeOffset? retryAt = null)
    {
        lock (gate)
        {
            if (Info(r, t) is not { } f) return;
            if (f.State != state) f.Since = DateTimeOffset.Now;
            f.State = state;
            f.RetryAt = retryAt;
            f.LastError = error ?? (state == FolderState.Watching ? null : f.LastError);
        }
    }

    public void Scanned(FolderRule r, Target t) { lock (gate) if (Info(r, t) is { } f) f.LastScan = DateTimeOffset.Now; }
    public void Queued() => Interlocked.Increment(ref pending);
    public void Dequeued() => Interlocked.Decrement(ref pending);

    // Regra nova ou pausa/retomada: o que seria feito pode ter mudado, então a simulação registra de novo. O aviso de
    // NF repetida não: é o mesmo arquivo parado na pasta e contaria de novo a cada mudança.
    public void ResetSeen() { lock (gate) simulatedSeen.Clear(); }

    // ------------------------------------------------------------------------------------------------- contadores
    public void Checked(FolderRule r, Verdict v, bool kept)
    {
        lock (gate)
        {
            var key = DayKey(DateTimeOffset.Now);
            saved.Checked++;
            Bump(saved.CheckedDaily, key);
            saved.FolderChecked[r.Path] = saved.FolderChecked.GetValueOrDefault(r.Path) + 1;
            if (kept && v == Verdict.Short) { saved.KeptShort++; Bump(saved.KeptShortDaily, key); }
            if (kept && v == Verdict.NoText) { saved.KeptNoText++; Bump(saved.KeptNoTextDaily, key); }
            dirty = true;
        }
    }

    // Simulação: registra o que seria feito. Cada arquivo entra uma vez (as varreduras o veem de novo).
    public bool Simulated(FolderRule r, string src, string reason, bool duplicate, string detail, bool processed = false)
    {
        lock (gate)
        {
            if (!Remember(simulatedSeen, SeenKey(r, src, reason))) return false;
            saved.Simulated++;
            Bump(saved.SimulatedDaily, DayKey(DateTimeOffset.Now));
            CountMinute(MinuteNow());
            Add(new Activity(DateTimeOffset.Now, nameof(ActivityKind.Simulated), Path.GetFileName(src),
                Path.GetDirectoryName(src) ?? r.Path, detail, reason));
            return true;
        }
    }

    // NF repetida em modo aviso: o arquivo fica; registra uma vez por arquivo.
    public bool Warned(FolderRule r, string src, string reason)
    {
        lock (gate)
        {
            if (!Remember(warnedSeen, SeenKey(r, src, reason))) return false;
            saved.Warnings++;
            Bump(saved.WarningsDaily, DayKey(DateTimeOffset.Now));
            Add(new Activity(DateTimeOffset.Now, nameof(ActivityKind.Warning), Path.GetFileName(src),
                Path.GetDirectoryName(src) ?? r.Path, "ficou na pasta (só aviso)", reason));
            return true;
        }
    }

    public void Done(FolderRule r, ActivityKind kind, string src, string? dest, string reason, bool duplicate, bool processed = false)
    {
        lock (gate)
        {
            var key = DayKey(DateTimeOffset.Now);
            if (processed) { saved.Processed++; Bump(saved.ProcessedDaily, key); }
            else
            {
                saved.Total++;
                Bump(saved.Daily, key);
                saved.FolderDone[r.Path] = saved.FolderDone.GetValueOrDefault(r.Path) + 1;
                if (duplicate) { saved.Duplicates++; Bump(saved.DuplicatesDaily, key); }
            }
            CountMinute(MinuteNow());
            Add(new Activity(DateTimeOffset.Now, kind.ToString(), Path.GetFileName(src), Path.GetDirectoryName(src) ?? r.Path, dest, reason));
        }
    }

    public void Failed(FolderRule r, string src, string reason)
    {
        lock (gate)
        {
            saved.Errors++;
            Bump(saved.ErrorsDaily, DayKey(DateTimeOffset.Now));
            Add(new Activity(DateTimeOffset.Now, nameof(ActivityKind.Error), Path.GetFileName(src), Path.GetDirectoryName(src) ?? r.Path, reason));
        }
    }

    // Pasta + nome (não o caminho inteiro): o vigia e a varredura podem escrever o mesmo caminho de jeitos diferentes
    // (ex.: nome curto 8.3 do Windows), e o mesmo arquivo contaria duas vezes.
    static string SeenKey(FolderRule r, string src, string reason) => r.Key + "|" + Path.GetFileName(src) + "|" + reason;

    static bool Remember(HashSet<string> set, string key)
    {
        if (set.Contains(key)) return false;
        if (set.Count >= MaxSeen) set.Clear();
        set.Add(key);
        return true;
    }

    static void Bump(SortedDictionary<string, int> daily, string key)
    {
        daily[key] = daily.GetValueOrDefault(key) + 1;
        while (daily.Count > MaxDays) daily.Remove(daily.Keys.First());
    }

    // ------------------------------------------------------------------------------------------------- painel
    public object Snapshot(JanitorOptions o, bool paused, object moveAll)
    {
        lock (gate)
        {
            var now = MinuteNow();
            var chart = new int[60];
            for (int k = 0; k < 60; k++)
            {
                var m = now - 59 + k;
                var i = (int)(m % 60);
                chart[k] = minuteStamp[i] == m ? perMinute[i] : 0;
            }
            var today = DateTimeOffset.Now;
            var t = DayKey(today);
            var daily = Enumerable.Range(0, 30).Select(k =>
            {
                var key = DayKey(today.AddDays(k - 29));
                return new
                {
                    day = key,
                    count = saved.Daily.GetValueOrDefault(key),
                    simulated = saved.SimulatedDaily.GetValueOrDefault(key),
                    processed = saved.ProcessedDaily.GetValueOrDefault(key),
                    @checked = saved.CheckedDaily.GetValueOrDefault(key),
                };
            }).ToList();
            return new
            {
                machine = Environment.MachineName,
                started,
                firstStart = saved.FirstStart,
                now = DateTimeOffset.Now,
                paused,
                dryRun = o.DryRun || folders.Values.Any(f => f.Rule.DryRun),
                globalDryRun = o.DryRun,
                rescanMinutes = o.RescanMinutes,
                total = saved.Total,
                today = saved.Daily.GetValueOrDefault(t),
                errors = saved.Errors,
                errorsToday = saved.ErrorsDaily.GetValueOrDefault(t),
                duplicates = saved.Duplicates,
                duplicatesToday = saved.DuplicatesDaily.GetValueOrDefault(t),
                warnings = saved.Warnings,
                warningsToday = saved.WarningsDaily.GetValueOrDefault(t),
                processed = saved.Processed,
                processedToday = saved.ProcessedDaily.GetValueOrDefault(t),
                simulated = saved.Simulated,
                simulatedToday = saved.SimulatedDaily.GetValueOrDefault(t),
                @checked = saved.Checked,
                checkedSince = saved.CheckedSince,
                checkedToday = saved.CheckedDaily.GetValueOrDefault(t),
                keptShortToday = saved.KeptShortDaily.GetValueOrDefault(t),
                keptNoTextToday = saved.KeptNoTextDaily.GetValueOrDefault(t),
                nfCache = nfCache.Count,
                nfCacheDays = nfCache.Days,
                pending = Math.Max(0, Volatile.Read(ref pending)),
                dataDir,
                perMinute = chart,
                daily,
                moveAll,
                folders = folderOrder.Select(k => folders[k]).Select(f => new
                {
                    path = f.Rule.Path,
                    action = f.Rule.IsMove ? "Move" : "Delete",
                    moveTo = f.Rule.MoveTo,
                    maxNameLength = f.Rule.MaxNameLength,
                    countExtension = f.Rule.CountExtension,
                    nameContains = f.Rule.NameContains,
                    includeSubdirectories = f.Rule.IncludeSubdirectories,
                    dryRun = f.Rule.DryRun,
                    cleanFolder = f.Rule.CleanFolder,
                    longNameRule = f.Rule.LongNameRule,
                    duplicateNf = f.Rule.DuplicateMode.ToString(),
                    nfPattern = f.Rule.NfPattern,
                    processedPath = f.Rule.ProcessedPath,
                    processedAuto = f.Rule.ProcessedAuto,
                    processedYesterday = f.Rule.ProcessedYesterday,
                    processedTarget = f.Rule.ProcessedPath == "" ? "" : f.Rule.ProcessedTarget,
                    processedToday = f.Rule.ProcessedDayFolders(DateTime.Now).FirstOrDefault() ?? "",
                    state = f.Watched.State.ToString(),
                    since = f.Watched.Since,
                    lastScan = f.Watched.LastScan,
                    retryAt = f.Watched.RetryAt,
                    lastError = f.Watched.LastError,
                    processedState = f.Rule.ProcessedPath == "" ? null : f.Processed.State.ToString(),
                    processedLastScan = f.Processed.LastScan,
                    processedRetryAt = f.Processed.RetryAt,
                    processedError = f.Processed.LastError,
                    done = saved.FolderDone.GetValueOrDefault(f.Rule.Path),
                    @checked = saved.FolderChecked.GetValueOrDefault(f.Rule.Path),
                }).ToList(),
                recent = saved.Recent.ToList(), // cópia: o JSON é gerado fora do lock, enquanto a lista muda
            };
        }
    }

    void Add(Activity a)
    {
        saved.Recent.Insert(0, a);
        if (saved.Recent.Count > MaxRecent) saved.Recent.RemoveRange(MaxRecent, saved.Recent.Count - MaxRecent);
        dirty = true;
        AppendHistory(a);
    }

    // ------------------------------------------------------------------------------------------------- histórico
    static readonly Dictionary<string, string> KindNames = new()
    {
        ["Deleted"] = "Apagado", ["Moved"] = "Movido", ["Simulated"] = "Simulado", ["Warning"] = "Aviso", ["Error"] = "Falha",
    };

    // Uma linha por arquivo tratado; abre no Excel (separador ";", UTF-8 com BOM).
    void AppendHistory(Activity a)
    {
        try
        {
            var file = Path.Combine(historyDir, DayKey(a.At) + ".csv");
            var header = File.Exists(file) ? "" : "﻿data_hora;acao;arquivo;pasta;detalhe\r\n";
            File.AppendAllText(file, header + string.Join(';',
                a.At.ToString("yyyy-MM-dd HH:mm:ss"), KindNames.GetValueOrDefault(a.Kind, a.Kind), Csv(a.Name), Csv(a.Folder),
                Csv(string.Join(" · ", new[] { a.Reason, a.Detail }.Where(x => !string.IsNullOrEmpty(x))))) + "\r\n", Encoding.UTF8);
        }
        catch (Exception ex) { log.LogError(ex, "Não deu para gravar o histórico em {Dir}", historyDir); }
    }

    static string Csv(string? s) =>
        s is null ? "" : s.IndexOfAny([';', '"', '\n', '\r']) < 0 ? s : '"' + s.Replace("\"", "\"\"") + '"';

    // O CSV de um dia (ou o .zip dele em historico\arquivados), mais novo primeiro.
    public object ReadHistory(string day, int limit = 2000)
    {
        var file = Path.Combine(historyDir, day + ".csv");
        var zip = Path.Combine(historyDir, "arquivados", day + ".csv.zip");
        string? text = null;
        try
        {
            if (File.Exists(file))
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                text = new StreamReader(fs, Encoding.UTF8).ReadToEnd();
            }
            else if (File.Exists(zip))
            {
                using var z = ZipFile.OpenRead(zip);
                using var r = new StreamReader(z.Entries[0].Open(), Encoding.UTF8);
                text = r.ReadToEnd();
            }
        }
        catch (Exception ex) { log.LogError(ex, "Não deu para ler o histórico de {Day}", day); }

        var lines = (text ?? "").Split('\n').Skip(1).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)
            .Select(ParseCsv).Where(p => p.Count >= 5)
            .Select(p => new { at = p[0], kind = p[1], name = p[2], folder = p[3], detail = p[4] }).ToList();
        lines.Reverse();
        return new { day, total = lines.Count, lines = lines.Take(limit) };
    }

    static List<string> ParseCsv(string line)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ';') { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    // CSV parado há zipAfterDays vira historico\arquivados\<dia>.csv.zip; zip com mais de keepZipDays é apagado.
    public void MaintainHistory(int zipAfterDays, int keepZipDays)
    {
        try
        {
            var archive = Path.Combine(historyDir, "arquivados");
            var today = DayKey(DateTimeOffset.Now);
            foreach (var csv in Directory.EnumerateFiles(historyDir, "*.csv"))
            {
                var name = Path.GetFileName(csv);
                if (name.StartsWith(today) || File.GetLastWriteTime(csv) > DateTime.Now.AddDays(-Math.Max(1, zipAfterDays))) continue;
                Directory.CreateDirectory(archive);
                var zip = Path.Combine(archive, name + ".zip");
                var tmp = zip + ".tmp";
                using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
                    z.CreateEntryFromFile(csv, name, CompressionLevel.Optimal);
                File.Move(tmp, zip, true);
                File.Delete(csv);
                log.LogInformation("Histórico {Name} arquivado em {Zip}", name, zip);
            }
            if (Directory.Exists(archive))
                foreach (var zip in Directory.EnumerateFiles(archive, "*.zip"))
                    if (File.GetLastWriteTime(zip) < DateTime.Now.AddDays(-Math.Max(1, keepZipDays)))
                    {
                        File.Delete(zip);
                        log.LogInformation("Histórico arquivado {Zip} apagado (mais de {Days} dias)", zip, keepZipDays);
                    }
        }
        catch (Exception ex) { log.LogError(ex, "Falha na manutenção do histórico"); }
    }

    static string DayKey(DateTimeOffset d) => d.ToString("yyyy-MM-dd");
    static long MinuteNow() => DateTimeOffset.Now.ToUnixTimeSeconds() / 60;
}

// Salva stats.json e nf-cache.json a cada 5 s quando algo mudou (e ao parar o serviço); de hora em hora arquiva
// o histórico antigo.
public sealed class StatsSaver(Stats stats, NfCache nfCache, Microsoft.Extensions.Options.IOptionsMonitor<JanitorOptions> opts) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastMaintenance = DateTime.MinValue;
        using var t = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await t.WaitForNextTickAsync(ct))
            {
                stats.Save();
                nfCache.Save();
                if (DateTime.Now - lastMaintenance > TimeSpan.FromHours(1))
                {
                    lastMaintenance = DateTime.Now;
                    stats.MaintainHistory(opts.CurrentValue.HistoryZipAfterDays, opts.CurrentValue.HistoryKeepZipDays);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        stats.Save();
        nfCache.Save();
    }
}
