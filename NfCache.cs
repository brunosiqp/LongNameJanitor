using System.Text.Json;
using Microsoft.Extensions.Options;

// NFs já vistas, por pasta: "pasta|número" -> primeiro arquivo daquela NF que ficou na pasta. Salvo em
// data\nf-cache.json (a cada 5 s, quando muda). Uma NF sai do cache depois de Janitor:NfCacheDays sem aparecer.
public sealed class NfCache
{
    public sealed class Entry
    {
        public string File { get; set; } = "";
        public DateTimeOffset First { get; set; }
        public DateTimeOffset Last { get; set; }
    }

    readonly Lock gate = new();
    readonly string path;
    readonly IOptionsMonitor<JanitorOptions> opts;
    readonly ILogger<NfCache> log;
    readonly Dictionary<string, Entry> map;
    DateTimeOffset lastPrune = DateTimeOffset.Now;
    bool dirty;

    public NfCache(IConfiguration config, IOptionsMonitor<JanitorOptions> opts, ILogger<NfCache> log)
    {
        this.opts = opts;
        this.log = log;
        var dataDir = Path.GetFullPath(config["Janitor:DataPath"] ?? "data", AppContext.BaseDirectory);
        Directory.CreateDirectory(dataDir);
        path = Path.Combine(dataDir, "nf-cache.json");
        map = Load();
        // v1.2 guardava só o número (cache global); agora é por pasta
        var legacy = map.Keys.Where(k => !k.Contains('|')).ToList();
        foreach (var k in legacy) map.Remove(k);
        if (legacy.Count > 0)
        {
            dirty = true;
            log.LogInformation("Cache de NF: {N} entrada(s) da versão anterior (sem pasta) descartadas", legacy.Count);
        }
        Prune();
    }

    TimeSpan Keep => TimeSpan.FromDays(Math.Clamp(opts.CurrentValue.NfCacheDays, 1, 365));
    public int Count { get { lock (gate) return map.Count; } }
    public int Days => (int)Keep.TotalDays;

    // Outro arquivo para uma NF já vista nesta pasta: devolve o nome do primeiro. Primeira vez (ou o mesmo arquivo
    // visto de novo): devolve null e, com register, guarda este arquivo como o primeiro.
    public string? Seen(string folderKey, string nf, string fileName, bool register)
    {
        var key = folderKey + "|" + nf;
        lock (gate)
        {
            var now = DateTimeOffset.Now;
            if (map.TryGetValue(key, out var e) && now - e.Last < Keep)
            {
                e.Last = now;
                dirty = true;
                return string.Equals(e.File, fileName, StringComparison.OrdinalIgnoreCase) ? null : e.File;
            }
            if (register)
            {
                map[key] = new Entry { File = fileName, First = now, Last = now };
                dirty = true;
            }
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
        var limit = DateTimeOffset.Now - Keep;
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
            text = JsonSerializer.Serialize(map);
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
