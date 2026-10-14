using Microsoft.Extensions.Options;

// "Mover tudo do Processado": percorre TODAS as pastas e subpastas da pasta de processados (não só a do dia) e
// move para ProcessedPath\LongNameJanitor, substituindo, todo arquivo com um texto de NameContains. Roda em segundo
// plano (pode ser o arquivo de anos num compartilhamento de rede), um de cada vez, e respeita a simulação.
public sealed class MoveAllJob(IOptionsMonitor<JanitorOptions> opts, Stats stats, ILogger<MoveAllJob> log)
{
    readonly Lock gate = new();
    bool running;
    string by = "", error = "";
    DateTimeOffset? start, end;
    bool dryRun;
    long folders, checkedFiles, moved, simulated, errors;

    public object Status
    {
        get
        {
            lock (gate)
                return new
                {
                    running, by, start, end, dryRun, error,
                    folders = Interlocked.Read(ref folders), @checked = Interlocked.Read(ref checkedFiles),
                    moved = Interlocked.Read(ref moved), simulated = Interlocked.Read(ref simulated), errors = Interlocked.Read(ref errors),
                };
        }
    }

    public string? Start(string who)
    {
        var o = opts.CurrentValue;
        var rules = o.Folders.Where((r, i) => r.ProcessedPath != "" && RuleValidator.Validate(r, i + 1) is null).ToList();
        if (rules.Count == 0) return "nenhuma pasta com ProcessedPath configurado";
        lock (gate)
        {
            if (running) return "já está movendo tudo do Processado; espere terminar";
            running = true;
            by = who;
            error = "";
            start = DateTimeOffset.Now;
            end = null;
            dryRun = o.DryRun || rules.Any(r => r.DryRun);
            folders = checkedFiles = moved = simulated = errors = 0;
        }
        log.LogWarning("Mover tudo do Processado pedido por {Who} ({Mode})", who, dryRun ? "simulação" : "movendo de verdade");
        _ = Task.Run(() => Run(rules, o.DryRun));
        return null;
    }

    void Run(List<FolderRule> rules, bool globalDryRun)
    {
        try
        {
            foreach (var r in rules)
            {
                var dry = globalDryRun || r.DryRun;
                if (!Directory.Exists(r.ProcessedPath)) throw new DirectoryNotFoundException($"pasta de processados inacessível: {r.ProcessedPath}");
                var pending = new Stack<string>([r.ProcessedPath]);
                while (pending.Count > 0)
                {
                    var dir = pending.Pop();
                    Interlocked.Increment(ref folders);
                    try
                    {
                        // a própria pasta LongNameJanitor não entra (senão mexeria no que já foi movido)
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                            if (!string.Equals(Path.TrimEndingDirectorySeparator(sub), Path.TrimEndingDirectorySeparator(r.ProcessedTarget), StringComparison.OrdinalIgnoreCase))
                                pending.Push(sub);
                        var rel = Path.GetRelativePath(r.ProcessedPath, dir);
                        foreach (var file in Directory.EnumerateFiles(dir).Order(StringComparer.OrdinalIgnoreCase).ToList())
                        {
                            Interlocked.Increment(ref checkedFiles);
                            if (!r.HasText(Path.GetFileName(file))) continue;
                            var reason = $"mover tudo do Processado ({rel})";
                            if (dry)
                            {
                                if (stats.Simulated(r, file, reason, false, $"Seria movido para {r.ProcessedTarget} (substitui se já existir)", processed: true))
                                    Interlocked.Increment(ref simulated);
                                continue;
                            }
                            try
                            {
                                var (dest, replaced) = FileOps.MoveReplacing(file, r.ProcessedTarget);
                                stats.Done(r, ActivityKind.Moved, file, dest + (replaced ? " (substituiu o que estava lá)" : ""), reason, false, processed: true);
                                Interlocked.Increment(ref moved);
                            }
                            catch (FileNotFoundException) { }
                            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)) { } // em uso: fica para depois
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                Interlocked.Increment(ref errors);
                                stats.Failed(r, file, ex is UnauthorizedAccessException ? "Sem permissão" : ex.Message);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Interlocked.Increment(ref errors);
                        log.LogWarning(ex, "Mover tudo: não deu para ler {Dir}", dir);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Mover tudo do Processado parou");
            lock (gate) error = ex.Message;
        }
        finally
        {
            lock (gate) { running = false; end = DateTimeOffset.Now; }
            log.LogWarning("Mover tudo do Processado ({Who}) terminou: {Folders} pasta(s), {Checked} verificado(s), {Moved} movido(s), {Sim} simulado(s), {Errors} erro(s)",
                by, folders, checkedFiles, moved, simulated, errors);
        }
    }
}
