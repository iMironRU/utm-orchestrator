using UtmOrchestrator.Core.Discovery;
using UtmOrchestrator.Core.Health;
using UtmOrchestrator.Service;

var builder = WebApplication.CreateBuilder(args);

// Работает как Windows-служба и как обычная консоль (для обкатки).
builder.Services.AddWindowsService(options => options.ServiceName = "UtmOrchestrator");
builder.Services.AddHostedService<HealthWorker>();

// Порт панели (по умолчанию 8090, не пересекается с УТМ 8080-8085 и их внутренними).
string url = builder.Configuration.GetValue("PanelUrl", "http://localhost:8090")!;
builder.WebHost.UseUrls(url);

var app = builder.Build();

app.UseDefaultFiles();   // отдавать index.html из wwwroot
app.UseStaticFiles();

// --- API статуса (read-only) ---
app.MapGet("/api/status", async (CancellationToken ct) =>
{
    var instances = await UtmDiscovery.DiscoverAsync(ct);
    var health = await new HealthChecker().CheckAsync(instances, ct);

    int ok = 0;
    var list = new List<object>();
    foreach (var h in health)
    {
        if (h.IsOk) ok++;
        list.Add(new
        {
            service = h.Instance.ServiceName,
            port = h.Instance.Port,
            fsrar = h.Info?.OwnerId ?? h.Instance.ExpectedFsrar,
            serial = h.Instance.TokenSerial,
            state = h.ServiceState.ToString(),
            verdict = h.Verdict.ToString(),
            reason = h.Reason,
            ok = h.IsOk,
            gost = h.Info?.GostValid ?? false,
        });
    }

    return Results.Json(new
    {
        total = health.Count,
        ok,
        faulty = health.Count - ok,
        instances = list,
    });
});

app.Run();
