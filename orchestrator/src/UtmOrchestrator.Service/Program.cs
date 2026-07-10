using UtmOrchestrator.Core.Diagnostics;
using UtmOrchestrator.Core.Discovery;
using UtmOrchestrator.Core.Health;
using UtmOrchestrator.Core.State;
using UtmOrchestrator.Service;

var builder = WebApplication.CreateBuilder(args);

// Работает как Windows-служба и как обычная консоль (для обкатки).
builder.Services.AddWindowsService(options => options.ServiceName = "UtmOrchestrator");
builder.Services.AddHostedService<BootBringUpWorker>(); // подъём УТМ на загрузке (peel-down)
builder.Services.AddHostedService<HealthWorker>();
builder.Services.AddSingleton<NameStore>();
builder.Services.AddSingleton<SerialCache>();
builder.Services.AddSingleton<OrgInfoCache>();

// Порт панели (по умолчанию 8090, не пересекается с УТМ 8080-8085 и их внутренними).
string url = builder.Configuration.GetValue("PanelUrl", "http://localhost:8090")!;
builder.WebHost.UseUrls(url);

var app = builder.Build();

app.UseDefaultFiles();   // отдавать index.html из wwwroot
app.UseStaticFiles();

// --- API статуса (read-only) ---
app.MapGet("/api/status", async (NameStore names, SerialCache serials, OrgInfoCache orgCache, CancellationToken ct) =>
{
    // scanTokens: false — НЕ трогаем PKCS11 на живых токенах (иначе драйвер роняет
    // процесс). Серийники берём из кэша SerialCache.
    var instances = await UtmDiscovery.DiscoverAsync(ct, scanTokens: false, serials);
    var health = await new HealthChecker().CheckAsync(instances, ct);

    using var http = new UtmHttpClient(TimeSpan.FromSeconds(5));

    int ok = 0;
    var list = new List<object>();
    foreach (var h in health)
    {
        if (h.IsOk) ok++;

        // Орг-данные из сертификата (адрес/организация) статичны — берём из кэша по
        // ФСРАР, по HTTP запрашиваем только при промахе и только если УТМ отвечает.
        UtmOrgInfo? org = null;
        string? fsrar = h.Info?.OwnerId ?? h.Instance.ExpectedFsrar;
        if (!string.IsNullOrEmpty(fsrar) && orgCache.TryGet(fsrar, out var cached))
        {
            org = cached;
        }
        else if (h.Instance.Port > 0 && h.Info is not null)
        {
            org = await http.GetOrgInfoAsync(h.Instance.Port, ct).ConfigureAwait(false);
            if (org is not null && !string.IsNullOrEmpty(fsrar)) orgCache.Set(fsrar, org);
        }

        string? customName = names.Get(h.Instance.TokenSerial);
        string? orgDisplay = org?.Display;
        // Заголовок: кастомное имя → орг/адрес → имя службы.
        string title = !string.IsNullOrWhiteSpace(customName) ? customName!
                     : !string.IsNullOrWhiteSpace(orgDisplay) ? orgDisplay!
                     : h.Instance.ServiceName;

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
            title,
            name = customName,       // кастомное краткое имя (или null)
            org = orgDisplay,        // адрес/организация из сертификата (или null)
            inn = org?.Inn,
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

// --- Задать/сбросить кастомное краткое имя УТМ (по серийнику) ---
app.MapPost("/api/utm/name", (SetNameRequest req, NameStore names) =>
{
    if (string.IsNullOrWhiteSpace(req.Serial))
        return Results.BadRequest(new { error = "serial обязателен" });
    names.Set(req.Serial, req.Name);
    return Results.Ok(new { ok = true });
});

// --- Обслуживание: разово пересканировать токены и обновить кэш серийников ---
// ВНИМАНИЕ: обращается к PKCS11 по подключённым токенам. На рабочей машине драйвер
// может уронить процесс, если токен занят живым УТМ. Выполнять осознанно, в окне
// обслуживания. Вынесено в отдельную команду, из горячего пути не вызывается.
app.MapPost("/api/tokens/rescan", async (SerialCache serials, OrgInfoCache orgCache, CancellationToken ct) =>
{
    await UtmDiscovery.DiscoverAsync(ct, scanTokens: true, serials);
    orgCache.Clear(); // перепривязка могла изменить орг-данные
    return Results.Ok(new { ok = true });
});

app.Run();

record SetNameRequest(string Serial, string? Name);
