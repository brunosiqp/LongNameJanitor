using System.Text.Json;

// NFs já vistas: número -> primeiro arquivo daquela NF que ficou na pasta. Salvo em data\nf-cache.json
// (a cada 5 s, junto com o stats.json). Uma NF sai do cache depois de Janitor:NfCacheDays sem aparecer.
public sealed class NfCache
{
    public sealed class Entry
    {
        public string File { get; set; } = "";
        public DateTimeOffset First { get; set; }
        public DateTimeOffset Last { get; set; }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    readonly Lock gate = new();
    readonly string path;
    readonly TimeSpan keep;
    readonly ILogger<NfCache> log;
    readonly Dictionary<string, Entry> map;
    DateTimeOffset lastPrune = DateTimeOffset.Now;
    bool dirty;

    public NfCache(IConfiguration config, ILogger<NfCache> log)
    {
        this.log = log;
        var dataDir = Path.GetFullPath(config["Janitor:DataPath"] ?? "data", AppContext.BaseDirectory);
        Directory.CreateDirectory(dataDir);
        path = Path.Combine(dataDir, "nf-cache.json");
        keep = TimeSpan.FromDays(Math.Max(1, config.GetValue("Janitor:NfCacheDays", 30)));
        map = Load();
        Prune();
    }

    public int Count { get { lock (gate) return map.Count; } }
    public int Days => (int)keep.TotalDays;

    // Primeira vez daquela NF (ou o mesmo arquivo visto de novo): guarda e devolve null.
    // Outro arquivo para uma NF já vista: devolve o nome do primeiro.
    public string? Register(string nf, string fileName)
    {
        lock (gate)
        {
            var now = DateTimeOffset.Now;
            dirty = true;
            if (map.TryGetValue(nf, out var e) && now - e.Last < keep)
            {
                e.Last = now;
                return string.Equals(e.File, fileName, StringComparison.OrdinalIgnoreCase) ? null : e.File;
            }
            map[nf] = new Entry { File = fileName, First = now, Last = now };
            return null;
        }
    }

    Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Não deu para ler {Path}; cache de NF começa vazio (cópia em .bad)", path);
            try { File.Copy(path, path + ".bad", true); } catch { }
        }
        return [];
    }

    void Prune()
    {
        var limit = DateTimeOffset.Now - keep;
        var old = map.Where(kv => kv.Value.Last < limit).Select(kv => kv.Key).ToList();
        foreach (var k in old) map.Remove(k);
        if (old.Count > 0) dirty = true;
        lastPrune = DateTimeOffset.Now;
    }

    public void Save()
    {
        string text;
        lock (gate)
        {
            if (DateTimeOffset.Now - lastPrune > TimeSpan.FromHours(1)) Prune();
            if (!dirty) return;
            text = JsonSerializer.Serialize(map, Json);
            dirty = false;
        }
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Não deu para salvar {Path}", path);
            lock (gate) dirty = true;
        }
    }
}
