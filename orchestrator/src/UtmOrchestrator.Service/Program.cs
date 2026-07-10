using UtmOrchestrator.Core.Diagnostics;
using UtmOrchestrator.Core.Discovery;
using UtmOrchestrator.Core.Health;
using UtmOrchestrator.Core.Recovery;
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

// --- Перезапуск одного УТМ через introduce (session-0-safe, без рестарта SCardSvr) ---
// Запускается в фоне (перезапуск ~50с): фронт увидит результат по опросу /api/status.
// Все операции с ридерами сериализуем общим замком (одна за раз).
app.MapPost("/api/utm/restart", (RestartRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Service))
        return Results.BadRequest(new { error = "service обязателен" });

    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var inst = state.Instances.FirstOrDefault(i =>
        string.Equals(i.ServiceName, req.Service, StringComparison.OrdinalIgnoreCase));
    if (inst is null) return Results.NotFound(new { error = $"УТМ {req.Service} не найден" });
    if (string.IsNullOrEmpty(inst.ReaderName))
        return Results.BadRequest(new { error = "нет ReaderName в конфиге — перезапуск через introduce невозможен" });

    if (!ReaderOp.Gate.Wait(0))
        return Results.Conflict(new { error = "уже идёт операция с ридерами — попробуйте позже" });

    var target = new BootBringUp.Target(inst.ServiceName, inst.Port, inst.TokenSerial ?? "", inst.ExpectedFsrar, inst.ReaderName);
    var allReaders = state.Instances.Select(i => i.ReaderName ?? "").Where(r => r.Length > 0).ToList();

    _ = Task.Run(() =>
    {
        try { BootBringUp.RestartOne(target, allReaders, ReaderOp.FileLog); }
        catch (Exception e) { ReaderOp.FileLog($"restart {req.Service}: СБОЙ — {e}"); }
        finally { ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, started = req.Service });
});

app.Run();

record SetNameRequest(string Serial, string? Name);
record RestartRequest(string Service);

// Сериализация операций с ридерами (перезапуск/подъём) + общий файловый лог.
static class ReaderOp
{
    public static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UtmOrchestrator", "bringup.log");
    public static void FileLog(string m)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} [api] {m}{Environment.NewLine}");
        }
        catch { }
    }
}
