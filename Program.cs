using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

// Serviço serve o painel e roda o Janitor em segundo plano. O ContentRoot é a pasta do .exe: como serviço,
// a pasta atual é system32 e o appsettings.json não seria achado.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
// Pastas e regras reais ficam em appsettings.local.json (fora do git), recarregado quando muda; a linha de
// comando ainda vence.
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true).AddCommandLine(args);
builder.Services.AddWindowsService(o => o.ServiceName = "LongNameJanitor");
builder.Services.Configure<JanitorOptions>(builder.Configuration.GetSection("Janitor"));
builder.Services.AddSingleton<NfCache>();
builder.Services.AddSingleton<Stats>();
builder.Services.AddSingleton<Control>();
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<MoveAllJob>();
builder.Services.AddHostedService<Janitor>();
builder.Services.AddHostedService<StatsSaver>();

if (!Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()) ConsoleMode.DisableQuickEdit();

var app = builder.Build();

string page;
using (var s = typeof(Janitor).Assembly.GetManifestResourceStream("dashboard.html")!)
using (var r = new StreamReader(s))
    page = r.ReadToEnd();

app.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));

// Gera o JSON aqui dentro para que qualquer erro vire uma mensagem legível no painel (e no log).
app.MapGet("/api/status", (Stats stats, Control control, MoveAllJob moveAll, IOptionsMonitor<JanitorOptions> opts, ILogger<Stats> log) =>
{
    try
    {
        var snapshot = stats.Snapshot(opts.CurrentValue, control.Paused, moveAll.Status);
        return Results.Content(JsonSerializer.Serialize(snapshot, JsonSerializerOptions.Web), "application/json");
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Falha ao montar o status do painel");
        return Results.Json(new { error = ex.GetType().Name + ": " + ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/history", (string? day, Stats stats) =>
{
    day ??= DateTime.Now.ToString("yyyy-MM-dd");
    if (!Regex.IsMatch(day, @"^\d{4}-\d{2}-\d{2}$")) return Results.BadRequest(new { error = "dia inválido" });
    return Results.Content(JsonSerializer.Serialize(stats.ReadHistory(day), JsonSerializerOptions.Web), "application/json");
});

// ---- administração: precisa do cabeçalho X-Admin-Key ----
var admin = app.MapGroup("/api/admin").AddEndpointFilter(async (ctx, next) =>
{
    var control = ctx.HttpContext.RequestServices.GetRequiredService<Control>();
    if (control.IsAdmin(ctx.HttpContext.Request)) return await next(ctx);
    await Task.Delay(500); // atrasa tentativa de adivinhar a chave
    return Results.Json(new { error = $"chave de administrador inválida (fica em {control.KeySource} no servidor)" }, statusCode: 403);
});

static string Who(HttpContext ctx) => "painel " + (ctx.Connection.RemoteIpAddress?.ToString() ?? "?");

admin.MapPost("/check", () => Results.Ok(new { ok = true }));

admin.MapGet("/config", (IOptionsMonitor<JanitorOptions> opts) =>
{
    var o = opts.CurrentValue;
    return Results.Content(JsonSerializer.Serialize(new
    {
        rescanMinutes = o.RescanMinutes,
        nfCacheDays = o.NfCacheDays,
        dryRun = o.DryRun,
        folders = o.Folders.Select(r => new
        {
            r.Path, Action = r.IsMove ? "Move" : "Delete", r.MoveTo, r.MaxNameLength, r.CountExtension, r.NameContains,
            r.IncludeSubdirectories, r.CleanFolder, r.LongNameRule, DuplicateNf = r.DuplicateMode.ToString(), r.NfPattern,
            r.ProcessedPath, r.ProcessedAuto, r.ProcessedYesterday, r.DryRun,
        }),
    }, JsonSerializerOptions.Web), "application/json");
});

admin.MapPost("/config", async (HttpContext ctx, ConfigStore store) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    return store.Save(body, Who(ctx)) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true });
});

admin.MapPost("/dryrun", async (HttpContext ctx, ConfigStore store) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    var on = body.TryGetProperty("dryRun", out var v) && v.ValueKind == JsonValueKind.True;
    return store.SetDryRun(on, Who(ctx)) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true });
});

admin.MapPost("/pause", async (HttpContext ctx, Control control) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    control.SetPaused(body.TryGetProperty("paused", out var v) && v.ValueKind == JsonValueKind.True, Who(ctx));
    return Results.Ok(new { ok = true });
});

admin.MapPost("/scan", (Control control) =>
{
    if (control.Paused) return Results.BadRequest(new { error = "está pausado: retome antes de varrer" });
    control.RequestScan();
    return Results.Ok(new { ok = true });
});

admin.MapPost("/move-all", (HttpContext ctx, MoveAllJob job) =>
    job.Start(Who(ctx)) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));

app.Run();
