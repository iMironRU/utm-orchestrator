using UtmOrchestrator.Core;
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
builder.Services.AddSingleton<PanelSettings>();
builder.Services.AddSingleton<UtmOrchestrator.Service.Jobs.JobStore>();

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
            folder = h.Instance.FolderPath,   // папка УТМ
            version = h.Info?.Version,        // версия УТМ (из /api/info/list)
            firewallOpen = OperatingSystem.IsWindows()
                && UtmOrchestrator.Core.Firewall.FirewallInspector.IsOpen(h.Instance.Port), // порт открыт в брандмауэре?
        });
    }

    return Results.Json(new
    {
        total = health.Count,
        ok,
        faulty = health.Count - ok,
        bringUp = BringUpStatus.Active, // идёт подъём/перепривязка — «не отвечает» это норма
        orchestratorVersion = UtmOrchestrator.Core.AppInfo.Version,
        instances = list,
    });
});

// --- Логи оркестратора (реальные): читаем bringup.log ---
app.MapGet("/api/logs", (int? limit) =>
{
    string path = AppPaths.BringupLog;
    var lines = new List<object>();
    try
    {
        if (File.Exists(path))
        {
            var all = File.ReadLines(path).ToList();
            int take = Math.Clamp(limit ?? 300, 10, 2000);
            foreach (var raw in all.Skip(Math.Max(0, all.Count - take)))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                // формат: "HH:mm:ss [src] сообщение"
                string time = raw.Length >= 8 && raw[2] == ':' ? raw.Substring(0, 8) : "";
                string msg = time.Length > 0 ? raw.Substring(8).TrimStart() : raw;
                string level =
                    msg.Contains('✗') || msg.Contains("СБОЙ") || msg.Contains("ОШИБКА") || msg.Contains("не поднялся") ? "error" :
                    msg.Contains("ВНИМАНИЕ") || msg.Contains("не тот") ? "warn" : "info";
                lines.Add(new { t = time, level, msg });
            }
        }
    }
    catch { /* лог недоступен — вернём пусто */ }
    lines.Reverse(); // новые сверху
    return Results.Json(new { lines });
});

// --- Настройки панели (реальные, persist) ---
app.MapGet("/api/settings", (PanelSettings settings) => Results.Json(settings.Load()));
app.MapPost("/api/settings", (PanelSettingsData data, PanelSettings settings) =>
{
    settings.Save(data);
    return Results.Ok(new { ok = true });
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
        using var _ = BringUpStatus.Begin(); // пока идёт перезапуск — «Запускается…», не «Сбой»
        try { BootBringUp.RestartOne(target, allReaders, ReaderOp.FileLog); }
        catch (Exception e) { ReaderOp.FileLog($"restart {req.Service}: СБОЙ — {e}"); }
        finally { ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, started = req.Service });
});

// --- Файрвол: открыть/закрыть порт УТМ (наше правило; служба = LocalSystem = админ) ---
app.MapPost("/api/utm/firewall", (FirewallRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Service)) return Results.BadRequest(new { error = "service обязателен" });
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var inst = state.Instances.FirstOrDefault(i =>
        string.Equals(i.ServiceName, req.Service, StringComparison.OrdinalIgnoreCase));
    if (inst is null) return Results.NotFound(new { error = $"УТМ {req.Service} не найден" });
    if (inst.Port <= 0) return Results.BadRequest(new { error = "у УТМ нет порта" });
    UtmOrchestrator.Core.Firewall.FirewallManager.SetPort(inst.Port, req.Open, ReaderOp.FileLog);
    return Results.Ok(new { ok = true, port = inst.Port, open = req.Open });
});

// --- Смена внешнего порта УТМ (session-0): конфиги + брандмауэр + introduce-рестарт ---
// В фоне под общим замком ридеров; фронт увидит результат по опросу /api/status.
app.MapPost("/api/utm/port", (ChangePortRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Service)) return Results.BadRequest(new { error = "service обязателен" });
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (req.NewPort < 1 || req.NewPort > 65535) return Results.BadRequest(new { error = "порт вне диапазона 1-65535" });

    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var inst = state.Instances.FirstOrDefault(i =>
        string.Equals(i.ServiceName, req.Service, StringComparison.OrdinalIgnoreCase));
    if (inst is null) return Results.NotFound(new { error = $"УТМ {req.Service} не найден" });
    if (string.IsNullOrEmpty(inst.ReaderName))
        return Results.BadRequest(new { error = "нет ReaderName — смена порта через introduce невозможна" });
    if (inst.Port == req.NewPort) return Results.BadRequest(new { error = "порт не изменился" });
    if (state.Instances.Any(i => i.Port == req.NewPort && !ReferenceEquals(i, inst)))
        return Results.Conflict(new { error = $"порт {req.NewPort} уже занят другим УТМ" });

    if (!ReaderOp.Gate.Wait(0))
        return Results.Conflict(new { error = "уже идёт операция с ридерами — попробуйте позже" });

    int oldPort = inst.Port;
    string folder = inst.FolderPath ?? "";
    var allReaders = state.Instances.Select(i => i.ReaderName ?? "").Where(r => r.Length > 0).ToList();

    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        try
        {
            var r = UtmOrchestrator.Core.Recovery.PortChanger.Change(
                folder, inst.ServiceName, oldPort, req.NewPort,
                inst.TokenSerial, inst.ExpectedFsrar, inst.ReaderName, allReaders, ReaderOp.FileLog);
            if (r.Success)
            {
                var st = OrchestratorState.Load(OrchestratorState.DefaultPath);
                var i2 = st.Instances.FirstOrDefault(i =>
                    string.Equals(i.ServiceName, req.Service, StringComparison.OrdinalIgnoreCase));
                if (i2 is not null) { i2.Port = req.NewPort; st.Save(OrchestratorState.DefaultPath); }
                ReaderOp.FileLog($"смена порта {req.Service}: успех ({oldPort}->{req.NewPort}), state.json обновлён");
            }
            else ReaderOp.FileLog($"смена порта {req.Service}: НЕ УДАЛОСЬ — {r.Message}");
        }
        catch (Exception e) { ReaderOp.FileLog($"смена порта {req.Service}: СБОЙ — {e}"); }
        finally { ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, service = req.Service, newPort = req.NewPort });
});

// --- Очередь интерактивных заданий (веб ↔ трей) ---
// Веб кладёт задание (scan/heal), трей (в интерактивной сессии) забирает pending,
// выполняет и возвращает результат, веб опрашивает по id. Только localhost.
app.MapPost("/api/jobs", (JobCreateRequest req, UtmOrchestrator.Service.Jobs.JobStore jobs) =>
{
    if (string.IsNullOrWhiteSpace(req.Type)) return Results.BadRequest(new { error = "type обязателен" });
    var job = jobs.Create(req.Type.Trim().ToLowerInvariant(), req.Params);
    return Results.Ok(new { id = job.Id });
});

app.MapGet("/api/jobs/pending", (UtmOrchestrator.Service.Jobs.JobStore jobs) =>
{
    var job = jobs.TakePending();
    return job is null
        ? Results.NoContent()
        : Results.Json(new { id = job.Id, type = job.Type, prms = job.Params });
});

app.MapPost("/api/jobs/{id}/result", (string id, JobResultRequest req, UtmOrchestrator.Service.Jobs.JobStore jobs) =>
{
    jobs.Complete(id, req.Result, req.Error);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/jobs/{id}", (string id, UtmOrchestrator.Service.Jobs.JobStore jobs) =>
{
    var job = jobs.Get(id);
    return job is null
        ? Results.NotFound(new { error = "нет такого задания" })
        : Results.Json(new { id = job.Id, type = job.Type, state = job.State.ToString(), result = job.Result, error = job.Error });
});

// --- Первый запуск / обследование: подхватить существующие УТМ ---
// state.json пуст = первый запуск. adopt строит state.json из discovery (службы
// Transport* + порт/папка/ФСРАР) + отсканированных треем токенов (серийник/ридер).
app.MapGet("/api/setup/status", () =>
{
    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    int withSerial = state.Instances.Count(i => !string.IsNullOrEmpty(i.TokenSerial));
    return Results.Json(new { adopted = state.Instances.Count, managed = withSerial, firstRun = state.Instances.Count == 0 });
});

app.MapPost("/api/setup/adopt", async (AdoptRequest req, SerialCache serials, CancellationToken ct) =>
{
    var byFsrar = new Dictionary<string, AdoptToken>(StringComparer.OrdinalIgnoreCase);
    foreach (var t in req.Tokens ?? new())
        if (!string.IsNullOrEmpty(t.Fsrar)) byFsrar[t.Fsrar!] = t;

    var instances = (await UtmDiscovery.DiscoverAsync(ct, scanTokens: false, serials)).ToList();
    int matched = 0;
    foreach (var inst in instances)
    {
        if (!string.IsNullOrEmpty(inst.ExpectedFsrar) && byFsrar.TryGetValue(inst.ExpectedFsrar!, out var tok))
        {
            inst.TokenSerial = tok.Serial;
            inst.ReaderName = tok.Reader;
            if (!string.IsNullOrEmpty(tok.Serial)) serials.Learn(inst.ExpectedFsrar!, tok.Serial!);
            matched++;
        }
    }
    new OrchestratorState { Instances = instances }.Save(OrchestratorState.DefaultPath);
    return Results.Ok(new { total = instances.Count, matched });
});

app.Run();

record SetNameRequest(string Serial, string? Name);
record RestartRequest(string Service);
record FirewallRequest(string Service, bool Open);
record ChangePortRequest(string Service, int NewPort);
record JobCreateRequest(string Type, string? Params);
record JobResultRequest(string? Result, string? Error);
record AdoptToken(string? Serial, string? Fsrar, string? Reader);
record AdoptRequest(List<AdoptToken>? Tokens);

// Сериализация операций с ридерами (перезапуск/подъём) + общий файловый лог.
static class ReaderOp
{
    public static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string LogPath = UtmOrchestrator.Core.AppPaths.BringupLog;
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
