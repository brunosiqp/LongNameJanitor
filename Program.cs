using System.Text.Json;

// Serviço serve o painel e roda o Janitor em segundo plano. O ContentRoot é a pasta do .exe: como serviço,
// a pasta atual é system32 e o appsettings.json não seria achado.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
// Pastas e regras reais ficam em appsettings.local.json (fora do git); a linha de comando ainda vence.
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true).AddCommandLine(args);
builder.Services.AddWindowsService(o => o.ServiceName = "LongNameJanitor");
builder.Services.Configure<JanitorOptions>(builder.Configuration.GetSection("Janitor"));
builder.Services.AddSingleton<Stats>();
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
app.MapGet("/api/status", (Stats stats, ILogger<Stats> log) =>
{
    try
    {
        return Results.Content(JsonSerializer.Serialize(stats.Snapshot(), JsonSerializerOptions.Web), "application/json");
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Falha ao montar o status do painel");
        return Results.Json(new { error = ex.GetType().Name + ": " + ex.Message }, statusCode: 500);
    }
});
app.Run();
