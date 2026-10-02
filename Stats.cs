using System.Text;
using System.Text.Json;

// Contadores e histórico do painel. Ficam salvos em data\stats.json (sobrevivem a reinícios) e cada arquivo
// tratado vira uma linha em data\historico\aaaa-MM-dd.csv.
public enum FolderState { Connecting, Watching, Reconnecting }
public enum ActivityKind { Deleted, Moved, Error }

public sealed record Activity(DateTimeOffset At, string Kind, string Name, string Folder, string? Detail);

public sealed class Stats
{
    const int MaxRecent = 300, MaxDays = 400;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    sealed class FolderInfo
    {
        public FolderState State = FolderState.Connecting;
        public DateTimeOffset Since = DateTimeOffset.Now;
        public DateTimeOffset? LastScan, RetryAt;
        public string? LastError;
    }

    sealed class Saved
    {
        public DateTimeOffset FirstStart { get; set; } = DateTimeOffset.Now;
        public long Total { get; set; }
        public long Errors { get; set; }
        public SortedDictionary<string, int> Daily { get; set; } = [];        // "2026-10-02" -> quantidade
        public Dictionary<string, long> FolderDone { get; set; } = [];        // pasta -> quantidade
        public List<Activity> Recent { get; set; } = [];
    }

    readonly Lock gate = new();
    readonly DateTimeOffset started = DateTimeOffset.Now;
    readonly string dataDir, statePath, historyDir;
    readonly ILogger<Stats> log;
    readonly Saved saved;
    readonly Dictionary<FolderRule, FolderInfo> folders = [];
    readonly int[] perMinute = new int[60];
    readonly long[] minuteStamp = new long[60];
    int pending;
    bool dirty;

    public Stats(IConfiguration config, ILogger<Stats> log)
    {
        this.log = log;
        dataDir = Path.GetFullPath(config["Janitor:DataPath"] ?? "data", AppContext.BaseDirectory);
        statePath = Path.Combine(dataDir, "stats.json");
        historyDir = Path.Combine(dataDir, "historico");
        Directory.CreateDirectory(historyDir);
        saved = Load();
        foreach (var a in saved.Recent.Where(a => a.Kind != nameof(ActivityKind.Error)))
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

    public void Register(FolderRule r) { lock (gate) folders[r] = new FolderInfo(); }

    public void SetState(FolderRule r, FolderState state, string? error = null, DateTimeOffset? retryAt = null)
    {
        lock (gate)
        {
            var f = folders[r];
            if (f.State != state) f.Since = DateTimeOffset.Now;
            f.State = state;
            f.RetryAt = retryAt;
            if (error != null) f.LastError = error;
        }
    }

    public void Scanned(FolderRule r) { lock (gate) folders[r].LastScan = DateTimeOffset.Now; }
    public void Queued() => Interlocked.Increment(ref pending);
    public void Dequeued() => Interlocked.Decrement(ref pending);

    public void Done(FolderRule r, ActivityKind kind, string src, string? dest)
    {
        lock (gate)
        {
            saved.Total++;
            var key = DayKey(DateTimeOffset.Now);
            saved.Daily[key] = saved.Daily.GetValueOrDefault(key) + 1;
            while (saved.Daily.Count > MaxDays) saved.Daily.Remove(saved.Daily.Keys.First());
            saved.FolderDone[r.Path] = saved.FolderDone.GetValueOrDefault(r.Path) + 1;
            CountMinute(MinuteNow());
            Add(new Activity(DateTimeOffset.Now, kind.ToString(), Path.GetFileName(src), r.Path, dest));
        }
    }

    public void Failed(FolderRule r, string src, string reason)
    {
        lock (gate)
        {
            saved.Errors++;
            Add(new Activity(DateTimeOffset.Now, nameof(ActivityKind.Error), Path.GetFileName(src), r.Path, reason));
        }
    }

    public object Snapshot()
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
            var daily = Enumerable.Range(0, 30).Select(k =>
            {
                var key = DayKey(today.AddDays(k - 29));
                return new { day = key, count = saved.Daily.GetValueOrDefault(key) };
            }).ToList();
            return new
            {
                machine = Environment.MachineName,
                started,
                firstStart = saved.FirstStart,
                now = DateTimeOffset.Now,
                total = saved.Total,
                today = saved.Daily.GetValueOrDefault(DayKey(today)),
                errors = saved.Errors,
                pending = Math.Max(0, Volatile.Read(ref pending)),
                dataDir,
                perMinute = chart,
                daily,
                folders = folders.Select(kv => new
                {
                    path = kv.Key.Path,
                    action = kv.Key.IsMove ? "Move" : "Delete",
                    moveTo = kv.Key.MoveTo,
                    maxNameLength = kv.Key.MaxNameLength,
                    countExtension = kv.Key.CountExtension,
                    nameContains = kv.Key.NameContains,
                    includeSubdirectories = kv.Key.IncludeSubdirectories,
                    state = kv.Value.State.ToString(),
                    since = kv.Value.Since,
                    lastScan = kv.Value.LastScan,
                    retryAt = kv.Value.RetryAt,
                    lastError = kv.Value.LastError,
                    done = saved.FolderDone.GetValueOrDefault(kv.Key.Path),
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

    // Uma linha por arquivo tratado; abre no Excel (separador ";", UTF-8 com BOM).
    void AppendHistory(Activity a)
    {
        try
        {
            var file = Path.Combine(historyDir, DayKey(a.At) + ".csv");
            var header = File.Exists(file) ? "" : "﻿data_hora;acao;arquivo;pasta;detalhe\r\n";
            var acao = a.Kind switch { "Deleted" => "Apagado", "Moved" => "Movido", _ => "Falha" };
            File.AppendAllText(file, header + string.Join(';',
                a.At.ToString("yyyy-MM-dd HH:mm:ss"), acao, Csv(a.Name), Csv(a.Folder), Csv(a.Detail)) + "\r\n", Encoding.UTF8);
        }
        catch (Exception ex) { log.LogError(ex, "Não deu para gravar o histórico em {Dir}", historyDir); }
    }

    static string Csv(string? s) =>
        s is null ? "" : s.IndexOfAny([';', '"', '\n', '\r']) < 0 ? s : '"' + s.Replace("\"", "\"\"") + '"';

    static string DayKey(DateTimeOffset d) => d.ToString("yyyy-MM-dd");
    static long MinuteNow() => DateTimeOffset.Now.ToUnixTimeSeconds() / 60;
}

// Salva o stats.json a cada 5 s quando algo mudou, e uma última vez ao parar o serviço.
public sealed class StatsSaver(Stats stats) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var t = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try { while (await t.WaitForNextTickAsync(ct)) stats.Save(); }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        stats.Save();
    }
}
