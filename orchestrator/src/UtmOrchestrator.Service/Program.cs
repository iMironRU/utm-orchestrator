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
            // Точная версия СБОРКИ (напр. 4.27.668) из SPA-бандла УТМ; запасной вариант
            // — версия формата из /api/info/list (4.2.0).
            version = UtmOrchestrator.Core.Diagnostics.UtmBuildVersion.Read(h.Instance.FolderPath) ?? h.Info?.Version,
            formatVersion = h.Info?.Version,  // версия формата (4.2.0)
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

// --- Перенос: ЭКСПОРТ УТМ в бандл (сторона-источник) ---
// Стоп службы → zip всей папки УТМ + манифест + procrun-реестр → introduce-возврат.
// Источник не разрушается. Бандл кладётся в <baseDir>\exports.
app.MapPost("/api/utm/export", (RestartRequest req) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (string.IsNullOrWhiteSpace(req.Service)) return Results.BadRequest(new { error = "service обязателен" });
    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var inst = state.Instances.FirstOrDefault(i =>
        string.Equals(i.ServiceName, req.Service, StringComparison.OrdinalIgnoreCase));
    if (inst is null) return Results.NotFound(new { error = $"УТМ {req.Service} не найден" });

    if (!ReaderOp.Gate.Wait(0))
        return Results.Conflict(new { error = "уже идёт операция с ридерами — попробуйте позже" });

    var allReaders = state.Instances.Select(i => i.ReaderName ?? "").Where(r => r.Length > 0).ToList();
    string exportsDir = Path.Combine(AppContext.BaseDirectory, "exports");

    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        try
        {
            var r = UtmOrchestrator.Core.Transfer.UtmTransfer.Export(inst, allReaders, null, exportsDir, ReaderOp.FileLog);
            ReaderOp.FileLog($"export {req.Service}: success={r.Success} — {r.Message} {r.BundlePath}");
        }
        catch (Exception e) { ReaderOp.FileLog($"export {req.Service}: СБОЙ — {e}"); }
        finally { ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, service = req.Service });
});

// --- Список готовых бандлов переноса ---
app.MapGet("/api/exports", () =>
{
    string dir = Path.Combine(AppContext.BaseDirectory, "exports");
    var list = new List<object>();
    if (Directory.Exists(dir))
        foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("UTM-export-*.zip")
                     .OrderByDescending(f => f.CreationTimeUtc))
            list.Add(new { name = fi.Name, sizeMb = fi.Length / 1_048_576, created = fi.CreationTimeUtc.ToString("o") });
    return Results.Json(new { exports = list });
});

// --- Скачать бандл переноса ---
app.MapGet("/api/exports/download", (string name) =>
{
    if (string.IsNullOrWhiteSpace(name) || name.Contains("..") || name.Contains('/') || name.Contains('\\'))
        return Results.BadRequest(new { error = "некорректное имя" });
    string path = Path.Combine(AppContext.BaseDirectory, "exports", name);
    if (!File.Exists(path)) return Results.NotFound(new { error = "бандл не найден" });
    return Results.File(path, "application/zip", name);
});

// --- Полечить токены: рестарт SCardSvr (будит замёрзшие) + introduce-подъём всех ---
// Служба (LocalSystem) делает это сама — UAC/трей не нужны. Это НЕ PKCS11-скан, а
// рестарт службы + introduce (session-0-safe, как boot-подъём). Фон, под общим замком.
app.MapPost("/api/utm/heal", () =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var targets = state.Instances
        .Where(i => !string.IsNullOrEmpty(i.TokenSerial))
        .Select(i => new BootBringUp.Target(i.ServiceName, i.Port, i.TokenSerial!, i.ExpectedFsrar, i.ReaderName))
        .ToList();
    if (targets.Count == 0) return Results.BadRequest(new { error = "нет привязок в state.json" });

    if (!ReaderOp.Gate.Wait(0))
        return Results.Conflict(new { error = "уже идёт операция с ридерами — попробуйте позже" });

    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        try
        {
            ReaderOp.FileLog("=== heal (лечение токенов) через службу запущен ===");
            UtmOrchestrator.Core.Readers.ReaderReset.ResetToNative(targets.Select(t => t.Service), ReaderOp.FileLog);
            var r = BootBringUp.ApplyIntroduce(targets, ReaderOp.FileLog);
            ReaderOp.FileLog($"heal: поднято {r.Started.Count}, ошибок {r.Failed.Count}, успех={r.Success}");
        }
        catch (Exception e) { ReaderOp.FileLog($"heal: СБОЙ — {e}"); }
        finally { ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, healing = true });
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

// --- Миграция с 2UTM: статус (детект + разбор config.ini + сопоставление с Transport) ---
app.MapGet("/api/2utm/status", async (SerialCache serials, CancellationToken ct) =>
{
    if (!OperatingSystem.IsWindows()) return Results.Json(new { present = false });
    string? folder = UtmOrchestrator.Core.Migration.TwoUtmConfig.FindFolder();
    if (folder is null) return Results.Json(new { present = false });

    var svc = UtmOrchestrator.Core.Migration.TwoUtmConfig.FindService();
    string? svcName = svc?.Name;
    string svcState = svcName is not null
        ? UtmOrchestrator.Core.Services.ServiceControl.GetState(svcName).ToString() : "NotInstalled";

    string? cfgPath = UtmOrchestrator.Core.Migration.TwoUtmConfig.FindConfigPath();
    var cfg = cfgPath is not null ? UtmOrchestrator.Core.Migration.TwoUtmConfig.Load(cfgPath) : null;

    var discovered = (await UtmDiscovery.DiscoverAsync(ct, scanTokens: false, serials)).ToList();
    var byPort = discovered.Where(i => i.Port > 0).ToDictionary(i => i.Port);

    var utms = new List<object>();
    int matched = 0;
    foreach (var u in cfg?.Utms ?? (IReadOnlyList<UtmOrchestrator.Core.Migration.TwoUtmConfig.Utm>)Array.Empty<UtmOrchestrator.Core.Migration.TwoUtmConfig.Utm>())
    {
        byPort.TryGetValue(u.Port, out var inst);
        if (inst is not null) matched++;
        utms.Add(new { index = u.Index, port = u.Port, serial = u.SerialHex, reader = u.AttrReader,
            matchedService = inst?.ServiceName, fsrar = inst?.ExpectedFsrar });
    }

    return Results.Json(new
    {
        present = true, folder,
        service = new { name = svcName, state = svcState, startMode = svc?.StartMode ?? "-" },
        autostart = cfg?.Autostart ?? false,
        count = cfg?.CountUtm ?? 0,
        matched, utms,
    });
});

// --- Перенять управление у 2UTM: adopt из config → state.json + заглушить 2UTM ---
app.MapPost("/api/2utm/adopt", async (SerialCache serials, CancellationToken ct) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    string? cfgPath = UtmOrchestrator.Core.Migration.TwoUtmConfig.FindConfigPath();
    var cfg = cfgPath is not null ? UtmOrchestrator.Core.Migration.TwoUtmConfig.Load(cfgPath) : null;
    if (cfg is null) return Results.NotFound(new { error = "config.ini 2UTM не найден" });

    if (!ReaderOp.Gate.Wait(0))
        return Results.Conflict(new { error = "уже идёт операция с ридерами — попробуйте позже" });
    try
    {
        var byPort = cfg.Utms.ToDictionary(u => u.Port);
        var instances = (await UtmDiscovery.DiscoverAsync(ct, scanTokens: false, serials)).ToList();
        int matched = 0;
        foreach (var inst in instances)
            if (byPort.TryGetValue(inst.Port, out var u))
            {
                inst.TokenSerial = u.SerialHex;
                inst.ReaderName = u.AttrReader;
                if (!string.IsNullOrEmpty(inst.ExpectedFsrar)) serials.Learn(inst.ExpectedFsrar!, u.SerialHex);
                matched++;
            }
        new OrchestratorState { Instances = instances }.Save(OrchestratorState.DefaultPath);
        ReaderOp.FileLog($"2UTM adopt: подхвачено {matched} из {cfg.Utms.Count}");

        // заглушить 2UTM (обратимо) — чтобы на загрузке не дрался с нами за ридеры.
        // Если служба есть — стоп + Disabled + autostart off; если службы нет (только
        // папка) — хотя бы autostart off, чтобы отразить «перенято».
        var svc = UtmOrchestrator.Core.Migration.TwoUtmConfig.FindService();
        if (svc is not null)
            UtmOrchestrator.Core.Migration.TwoUtmControl.Disable(svc.Value.Name, cfgPath, ReaderOp.FileLog);
        else if (cfgPath is not null)
            UtmOrchestrator.Core.Migration.TwoUtmConfig.SetAutostart(cfgPath, false, ReaderOp.FileLog);

        return Results.Ok(new { ok = true, matched, total = cfg.Utms.Count, disabled = svc?.Name });
    }
    finally { ReaderOp.Gate.Release(); }
});

// --- Вернуть 2UTM (откат миграции): Automatic + autostart=true + старт ---
app.MapPost("/api/2utm/restore", () =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    string? cfgPath = UtmOrchestrator.Core.Migration.TwoUtmConfig.FindConfigPath();
    if (cfgPath is null && UtmOrchestrator.Core.Migration.TwoUtmConfig.FindFolder() is null)
        return Results.NotFound(new { error = "2UTM не найден" });
    var svc = UtmOrchestrator.Core.Migration.TwoUtmConfig.FindService();
    if (svc is not null)
        UtmOrchestrator.Core.Migration.TwoUtmControl.Restore(svc.Value.Name, cfgPath, ReaderOp.FileLog);
    else if (cfgPath is not null)
        UtmOrchestrator.Core.Migration.TwoUtmConfig.SetAutostart(cfgPath, true, ReaderOp.FileLog);
    return Results.Ok(new { ok = true, restored = svc?.Name });
});

// --- Самообновление оркестратора: статус ---
app.MapGet("/api/update/status", async (CancellationToken ct) =>
{
    var info = await UtmOrchestrator.Core.Update.UpdateChecker.CheckAsync(ct);
    return Results.Json(new { current = info.Current, latest = info.Latest, updateAvailable = info.UpdateAvailable });
});

// --- Самообновление: применить (скачать payload → распаковать → detached update.ps1) ---
// update.ps1 остановит службу (наш родитель), заменит файлы и стартанёт заново; он —
// отдельный процесс, поэтому переживёт остановку службы. Панель на ~минуту пропадёт.
app.MapPost("/api/update/apply", async (CancellationToken ct) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    var info = await UtmOrchestrator.Core.Update.UpdateChecker.CheckAsync(ct);
    if (!info.UpdateAvailable || info.PayloadUrl is null)
        return Results.BadRequest(new { error = "обновление недоступно" });

    _ = Task.Run(async () =>
    {
        try
        {
            string tmp = Path.Combine(Path.GetTempPath(), "utmo-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            string zip = Path.Combine(tmp, "payload.zip");
            ReaderOp.FileLog($"update: качаю {info.PayloadUrl}");
            using (var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var resp = await h.GetAsync(info.PayloadUrl, HttpCompletionOption.ResponseHeadersRead))
            using (var src = await resp.Content.ReadAsStreamAsync())
            using (var dst = File.Create(zip))
                await src.CopyToAsync(dst);

            string staging = Path.Combine(tmp, "staging");
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, staging);
            string updatePs1 = Path.Combine(staging, "update.ps1");
            if (!File.Exists(updatePs1)) { ReaderOp.FileLog("update: update.ps1 нет в payload"); return; }

            ReaderOp.FileLog($"update: запускаю {updatePs1} (служба перезапустится)");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{updatePs1}\"")
            { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e) { ReaderOp.FileLog($"update: СБОЙ — {e}"); }
    });
    return Results.Accepted(value: new { ok = true, updating = info.Latest });
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
