using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Estado que o painel controla: pausa (data\estado.json), pedido de varredura e a chave de administrador.
// A chave vem de Dashboard:AdminKey; sem ela, o programa gera uma e guarda em data\admin-key.txt (só quem tem
// acesso ao servidor lê). Sem a chave certa, o painel só mostra: não pausa, não muda pastas, não move nada.
public sealed class Control
{
    sealed class State
    {
        public bool Paused { get; set; }
        public string? ChangedBy { get; set; }
        public DateTimeOffset? ChangedAt { get; set; }
    }

    readonly string statePath;
    readonly ILogger<Control> log;
    readonly byte[] adminKey;
    State state;

    public event Action? Changed;
    public event Action? ScanRequested;

    public Control(IConfiguration config, Stats stats, ILogger<Control> log)
    {
        this.log = log;
        statePath = Path.Combine(stats.DataDir, "estado.json");
        state = Load();

        var key = config["Dashboard:AdminKey"];
        var keyFile = Path.Combine(stats.DataDir, "admin-key.txt");
        KeySource = "Dashboard:AdminKey";
        if (string.IsNullOrWhiteSpace(key))
        {
            KeySource = keyFile;
            try
            {
                if (File.Exists(keyFile)) key = File.ReadAllText(keyFile).Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'x').Replace('/', 'y');
                    File.WriteAllText(keyFile, key);
                    log.LogWarning("Chave de administrador do painel criada em {File}", keyFile);
                }
            }
            catch (Exception ex) { log.LogError(ex, "Não deu para ler/criar {File}: painel fica só leitura", keyFile); }
        }
        adminKey = Encoding.UTF8.GetBytes(key ?? "");
    }

    public string KeySource { get; }
    public bool Paused => state.Paused;
    public object Info => new { paused = state.Paused, changedBy = state.ChangedBy, changedAt = state.ChangedAt };

    public bool IsAdmin(HttpRequest req)
    {
        var given = Encoding.UTF8.GetBytes(req.Headers["X-Admin-Key"].ToString());
        return adminKey.Length > 0 && CryptographicOperations.FixedTimeEquals(given, adminKey);
    }

    public void SetPaused(bool paused, string who)
    {
        state = new State { Paused = paused, ChangedBy = who, ChangedAt = DateTimeOffset.Now };
        Save();
        log.LogWarning("{What} pelo painel ({Who})", paused ? "PAUSADO" : "Retomado", who);
        Changed?.Invoke();
    }

    public void RequestScan() => ScanRequested?.Invoke();

    State Load()
    {
        try { if (File.Exists(statePath)) return JsonSerializer.Deserialize<State>(File.ReadAllText(statePath)) ?? new(); }
        catch (Exception ex) { log.LogError(ex, "Não deu para ler {Path}", statePath); }
        return new();
    }

    void Save()
    {
        try
        {
            File.WriteAllText(statePath + ".tmp", JsonSerializer.Serialize(state));
            File.Move(statePath + ".tmp", statePath, true);
        }
        catch (Exception ex) { log.LogError(ex, "Não deu para salvar {Path}", statePath); }
    }
}

// Regras de uma pasta: as mesmas na leitura da configuração e no editor do painel.
public static class RuleValidator
{
    public const int MaxFolders = 30;
    static readonly Regex PathRe = new(@"^(\\\\[^\\/:*?""<>|\r\n]+\\[^:*?""<>|\r\n]+|[A-Za-z]:\\[^:*?""<>|\r\n]*)$");

    public static bool ValidPath(string p) => PathRe.IsMatch(p);

    public static string? Validate(FolderRule r, int n)
    {
        var path = r.Path.Trim().TrimEnd('\\');
        if (!ValidPath(path)) return $"pasta {n}: caminho inválido (use \\\\servidor\\compartilhamento\\... ou C:\\...)";
        if (!r.IsMove && !r.Action.Equals("Delete", StringComparison.OrdinalIgnoreCase)) return $"pasta {n}: Action tem que ser Delete ou Move";
        if (r.IsMove && !ValidPath(r.MoveTo.Trim().TrimEnd('\\'))) return $"pasta {n}: Move precisa de um MoveTo válido";
        if (r.IsMove && Same(r.MoveTo, path)) return $"pasta {n}: MoveTo não pode ser a própria pasta";
        if (r.MaxNameLength is < 10 or > 250) return $"pasta {n}: MaxNameLength entre 10 e 250";
        // sem texto obrigatório, qualquer arquivo com nome longo seria apagado: perigoso demais para deixar sem querer
        if (!r.NameContains.Any(s => !string.IsNullOrWhiteSpace(s))) return $"pasta {n}: informe pelo menos um texto em NameContains (ex.: o CNPJ)";
        if (!string.IsNullOrEmpty(r.DuplicateNf) && !Enum.TryParse<DuplicateMode>(r.DuplicateNf, true, out _))
            return $"pasta {n}: DuplicateNf tem que ser Off, Warn ou Delete";
        try { if (new Regex(r.NfPattern).GetGroupNumbers().Length < 2) return $"pasta {n}: NfPattern precisa de um grupo, ex.: ^NF0*(\\d+)_"; }
        catch (ArgumentException) { return $"pasta {n}: NfPattern inválido"; }
        var processed = r.ProcessedPath.Trim().TrimEnd('\\');
        if (processed != "" && !ValidPath(processed)) return $"pasta {n}: ProcessedPath inválido";
        if (processed != "" && Same(processed, path)) return $"pasta {n}: ProcessedPath não pode ser a própria pasta vigiada";
        return null;
    }

    static bool Same(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

// Grava pastas e opções em appsettings.local.json (ao lado do .exe), preservando o resto do arquivo. O programa
// recarrega sozinho quando o arquivo muda (editado aqui ou à mão).
public sealed class ConfigStore(ILogger<ConfigStore> log)
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    readonly string path = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
    readonly Lock gate = new();

    public string? Save(JsonElement body, string who)
    {
        List<FolderRule> folders;
        int rescan, cacheDays;
        try
        {
            folders = body.GetProperty("folders").Deserialize<List<FolderRule>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            rescan = body.GetProperty("rescanMinutes").GetInt32();
            cacheDays = body.GetProperty("nfCacheDays").GetInt32();
        }
        catch (Exception) { return "configuração inválida"; }

        if (folders.Count > RuleValidator.MaxFolders) return $"até {RuleValidator.MaxFolders} pastas";
        for (int i = 0; i < folders.Count; i++)
        {
            var r = folders[i];
            r.Path = r.Path.Trim().TrimEnd('\\');
            r.MoveTo = r.IsMove ? r.MoveTo.Trim().TrimEnd('\\') : "";
            r.ProcessedPath = r.ProcessedPath.Trim().TrimEnd('\\');
            r.NameContains = r.NameContains.Select(s => s.Trim()).Where(s => s != "").Take(10).ToList();
            r.DeleteDuplicateNf = null;
            if (RuleValidator.Validate(r, i + 1) is { } error) return error;
        }
        if (folders.Select(f => f.Key).Distinct().Count() != folders.Count) return "tem pasta repetida";
        if (rescan is < 1 or > 1440) return "varredura de 1 a 1440 minutos";
        if (cacheDays is < 1 or > 365) return "cache de NF de 1 a 365 dias";

        return Write(janitor =>
        {
            janitor["RescanMinutes"] = rescan;
            janitor["NfCacheDays"] = cacheDays;
            janitor["Folders"] = new JsonArray(folders.Select(ToNode).ToArray());
        }, $"pastas alteradas por {who}: {folders.Count} pasta(s)");
    }

    public string? SetDryRun(bool dryRun, string who) =>
        Write(janitor => janitor["DryRun"] = dryRun, dryRun ? $"simulação LIGADA por {who}" : $"simulação DESLIGADA por {who} (apagando de verdade)");

    static JsonNode ToNode(FolderRule r)
    {
        var o = new JsonObject
        {
            ["Path"] = r.Path,
            ["Action"] = r.IsMove ? "Move" : "Delete",
            ["MaxNameLength"] = r.MaxNameLength,
            ["CountExtension"] = r.CountExtension,
            ["NameContains"] = new JsonArray(r.NameContains.Select(s => (JsonNode)s!).ToArray()),
            ["IncludeSubdirectories"] = r.IncludeSubdirectories,
            ["CleanFolder"] = r.CleanFolder,
            ["LongNameRule"] = r.LongNameRule,
            ["DuplicateNf"] = r.DuplicateMode.ToString(),
            ["NfPattern"] = r.NfPattern,
            ["ProcessedPath"] = r.ProcessedPath,
            ["ProcessedAuto"] = r.ProcessedAuto,
            ["ProcessedYesterday"] = r.ProcessedYesterday,
            ["DryRun"] = r.DryRun,
        };
        if (r.IsMove) o["MoveTo"] = r.MoveTo;
        return o;
    }

    string? Write(Action<JsonObject> change, string what)
    {
        lock (gate)
        {
            try
            {
                var root = File.Exists(path)
                    ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? []
                    : [];
                if (root["Janitor"] is not JsonObject janitor) root["Janitor"] = janitor = [];
                change(janitor);
                if (File.Exists(path)) File.Copy(path, path + ".bak", true);
                File.WriteAllText(path + ".tmp", root.ToJsonString(Pretty));
                File.Move(path + ".tmp", path, true);
                log.LogWarning("appsettings.local.json: {What}", what);
                return null;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Não deu para gravar {Path}", path);
                return "não deu para gravar a configuração (detalhes no log)";
            }
        }
    }
}
