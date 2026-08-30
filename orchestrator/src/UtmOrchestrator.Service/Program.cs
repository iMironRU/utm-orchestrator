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

// Порт панели (8090). Биндимся ВСЕГДА на 0.0.0.0, а доступ извне гейтит middleware
// (когда NetworkAccess выкл — пускаем только localhost). Так смена настройки применяется
// без перезапуска службы. Явный PanelUrl из конфига перекрывает.
string url = builder.Configuration.GetValue<string?>("PanelUrl", null) ?? "http://0.0.0.0:8090";
builder.WebHost.UseUrls(url);

// Лимит числа УТМ на одну машину (по числу токенов/ридеров). Настраивается в
// appsettings (MaxUtms), по умолчанию 10. Показываем в панели и не даём превысить.
int maxUtms = builder.Configuration.GetValue("MaxUtms", 10);

// Сериализация импорта бандлов между собой (распаковка + регистрация службы + запись
// state.json). НЕ трогает ридеры, поэтому это отдельный лёгкий гейт, не ReaderOp.Gate —
// иначе импорт блокировал бы статус/подъём и последовательная загрузка бандлов упиралась
// бы в занятый общий замок.
var importGate = new SemaphoreSlim(1, 1);

var app = builder.Build();

// --- Доступ: IP-allowlist + серверная авторизация (когда включены) ---
app.Use(async (ctx, next) =>
{
    var s = ctx.RequestServices.GetRequiredService<PanelSettings>().Current;
    var ip = ctx.Connection.RemoteIpAddress;
    bool isLocal = ip is not null && System.Net.IPAddress.IsLoopback(ip);

    // Доступ по сети выключен → пускаем только localhost (бинд всегда 0.0.0.0).
    if (!s.NetworkAccess && !isLocal)
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsync("доступ по сети выключен");
        return;
    }

    // IP-allowlist: если задан список и не localhost и не в списке — отказ.
    if (!isLocal && s.AllowedIps.Count > 0 && !s.AllowedIps.Contains(ip?.ToString() ?? ""))
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsync("IP не разрешён");
        return;
    }

    // Авторизация: RequireAuth → /api/* (кроме входа) требуют вход, но ТОЛЬКО по сети.
    // С localhost вход не спрашиваем: оператор за машиной доверенный, трей читает статус
    // без куки, и нет риска локаута (можно сбросить пароль локально).
    string path = ctx.Request.Path.Value ?? "";
    // /api/update/status публичен: оверлей обновления опрашивает версию после рестарта
    // службы, когда сессия в памяти уже сброшена — иначе он зависает на 401.
    if (s.RequireAuth && !isLocal && path.StartsWith("/api/")
        && path != "/api/auth/login" && path != "/api/update/status")
    {
        if (!PanelAuth.Valid(ctx.Request.Cookies[PanelAuth.Cookie]))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("нужен вход");
            return;
        }
    }
    await next();
});

app.UseDefaultFiles();   // отдавать index.html из wwwroot
// no-cache: браузер обязан ревалидировать (ETag → 304, если не менялось). Иначе после
// самообновления оркестратора старый app.js/index.html «залипает» в кэше и новый UI не виден.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
    }
});

// --- Вход/выход в панель ---
app.MapPost("/api/auth/login", (LoginRequest req, PanelSettings settings, HttpContext ctx) =>
{
    var s = settings.Current;
    if (!s.RequireAuth) return Results.Ok(new { ok = true }); // вход не требуется
    bool userOk = string.IsNullOrEmpty(s.Username)
        || string.Equals(s.Username, req.Username, StringComparison.OrdinalIgnoreCase);
    if (userOk && PanelPassword.Verify(req.Password ?? "", s.PasswordHash, s.PasswordSalt))
    {
        ctx.Response.Cookies.Append(PanelAuth.Cookie, PanelAuth.Issue(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(7),
        });
        return Results.Ok(new { ok = true });
    }
    return Results.Json(new { ok = false, error = "неверный логин или пароль" }, statusCode: 401);
});

app.MapPost("/api/auth/logout", (HttpContext ctx) =>
{
    PanelAuth.Revoke(ctx.Request.Cookies[PanelAuth.Cookie]);
    ctx.Response.Cookies.Delete(PanelAuth.Cookie);
    return Results.Ok(new { ok = true });
});

// --- API статуса (read-only) ---
app.MapGet("/api/status", async (NameStore names, SerialCache serials, OrgInfoCache orgCache, CancellationToken ct) =>
{
    // БЫСТРЫЙ ПУТЬ во время подъёма: не ходим по HTTP к ещё не поднятым УТМ (это заняло
    // бы десятки секунд и панель/трей «висели» бы). Отдаём живой прогресс из BootProgress:
    // кого уже подняли — Ok, остальные — «Запускается…», + фаза и прогноз (ETA).
    var boot = BootProgress.Get();
    if (BringUpStatus.Active && boot.Active)
    {
        var st = UtmOrchestrator.Core.State.OrchestratorState.Load(
            UtmOrchestrator.Core.State.OrchestratorState.DefaultPath);
        var ready = boot.ReadyServices;
        var bootList = st.Instances.Select(i =>
        {
            // Три состояния: поднят / поднимается сейчас / ждёт в очереди.
            bool up = ready.Contains(i.ServiceName);
            bool now = !up && string.Equals(i.ServiceName, boot.Current, StringComparison.OrdinalIgnoreCase);
            string verdict = up ? "Ok" : now ? "Starting" : "Queued";
            return (object)new
            {
                service = i.ServiceName,
                port = i.Port,
                verdict,
                ok = up,
                title = names.Get(i.TokenSerial) ?? i.ServiceName,
                reason = up ? null : now ? "Запускается…" : "В очереди",
                version = UtmOrchestrator.Core.Diagnostics.UtmBuildVersion.Read(i.FolderPath), // чтобы версия не пропадала при операции
            };
        }).ToList();
        return Results.Json(new
        {
            total = st.Instances.Count,
            ok = boot.Ready,
            faulty = 0,
            bringUp = true,
            boot = new
            {
                active = true,
                ready = boot.Ready,
                total = boot.Total,
                phase = boot.Phase,
                elapsedSeconds = boot.ElapsedSeconds,
                etaRemainingSeconds = boot.EtaRemainingSeconds,
            },
            orchestratorVersion = UtmOrchestrator.Core.AppInfo.Version,
            maxUtms,
            instances = bootList,
        });
    }

    // Быстрый путь для длинной операции (установка/привязка): отдаём живой прогресс из
    // OpProgress БЕЗ HTTP-опроса УТМ — панель показывает «ставлю N/M: … — фаза», а не «ждите».
    var opNow = OpProgress.Get();
    if (opNow.Active)
    {
        var st = UtmOrchestrator.Core.State.OrchestratorState.Load(
            UtmOrchestrator.Core.State.OrchestratorState.DefaultPath);
        var opList = st.Instances.Select(i => (object)new
        {
            service = i.ServiceName, port = i.Port, verdict = "Starting", ok = false,
            title = names.Get(i.TokenSerial) ?? i.ServiceName, reason = "Идёт установка…",
            version = UtmOrchestrator.Core.Diagnostics.UtmBuildVersion.Read(i.FolderPath), // версия не пропадает при операции
        }).ToList();
        return Results.Json(new
        {
            total = st.Instances.Count,
            ok = 0,
            faulty = 0,
            bringUp = true,
            op = new { active = true, title = opNow.Title, total = opNow.Total, done = opNow.Done, phase = opNow.Phase, current = opNow.Current },
            orchestratorVersion = UtmOrchestrator.Core.AppInfo.Version,
            maxUtms,
            instances = opList,
        });
    }

    // Идёт операция с ридерами/УТМ (перенос/привязка/обновление/перезапуск/лечение), но НЕ boot и
    // НЕ install-op: НЕ делаем медленный discovery+health (УТМ может быть остановлен, health висел бы,
    // а список «пропадал» бы). Отдаём быстрый статус из state.json (+version), bringUp=true → панель
    // МГНОВЕННО показывает спиннер «идёт операция», и список УТМ на месте.
    if (BringUpStatus.Active)
    {
        var stb = UtmOrchestrator.Core.State.OrchestratorState.Load(UtmOrchestrator.Core.State.OrchestratorState.DefaultPath);
        string utmRootB = Path.GetFullPath(UtmOrchestrator.Core.AppPaths.UtmRoot).TrimEnd(Path.DirectorySeparatorChar);
        var busyList = stb.Instances.Select(i => (object)new
        {
            service = i.ServiceName, port = i.Port, verdict = "Starting", ok = false,
            title = names.Get(i.TokenSerial) ?? i.ServiceName, reason = "идёт операция…",
            version = UtmOrchestrator.Core.Diagnostics.UtmBuildVersion.Read(i.FolderPath),
            inOurFolder = !string.IsNullOrEmpty(i.FolderPath)
                && Path.GetFullPath(i.FolderPath).TrimEnd(Path.DirectorySeparatorChar)
                    .StartsWith(utmRootB, StringComparison.OrdinalIgnoreCase),
        }).ToList();
        return Results.Json(new
        {
            total = stb.Instances.Count, ok = 0, faulty = 0, bringUp = true,
            orchestratorVersion = UtmOrchestrator.Core.AppInfo.Version,
            machine = Environment.MachineName, maxUtms, instances = busyList,
        });
    }

    // scanTokens: false — НЕ трогаем PKCS11 на живых токенах (иначе драйвер роняет
    // процесс). Серийники берём из кэша SerialCache.
    var instances = await UtmDiscovery.DiscoverAsync(ct, scanTokens: false, serials);
    var health = await new HealthChecker().CheckAsync(instances, ct);

    // Внешние порты (проброс на роутере) хранятся в state.json — обнаружение их не знает.
    var stored = UtmOrchestrator.Core.State.OrchestratorState.Load(
        UtmOrchestrator.Core.State.OrchestratorState.DefaultPath);
    var extPorts = stored.Instances
        .Where(i => i.ExternalPort.HasValue)
        .GroupBy(i => i.ServiceName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().ExternalPort!.Value, StringComparer.OrdinalIgnoreCase);

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
        // Юрлицо/владелец: организация (ООО) → ФИО владельца (ИП). Показываем ВСЕГДА, даже
        // при кастомной подписи — на машине могут быть УТМ разных юрлиц, и по подписи их не
        // различить. Группировка в интерфейсе — по ИНН.
        string? entity = !string.IsNullOrWhiteSpace(org?.Organization) ? org!.Organization
                       : !string.IsNullOrWhiteSpace(org?.PersonName) ? org!.PersonName : null;
        // Заголовок: кастомное имя → орг/адрес → имя службы.
        string title = !string.IsNullOrWhiteSpace(customName) ? customName!
                     : !string.IsNullOrWhiteSpace(orgDisplay) ? orgDisplay!
                     : h.Instance.ServiceName;

        // Реальный статус обмена (из transport_info.log), кэш ~20с — файл читаем не на
        // каждый опрос. Только если УТМ отвечает (иначе лог не про обмен, а про подъём).
        var ex = h.IsOk ? ExchangeCache.Get(h.Instance.FolderPath) : null;
        // Счётчики очередей: входящие (/opt/out) и исходящие (/opt/in) — для «безопасно ли
        // останавливать/переносить». -1 = не смогли спросить.
        var q = h.IsOk ? QueueCache.Get(h.Instance.Port) : (-1, -1);

        list.Add(new
        {
            service = h.Instance.ServiceName,
            port = h.Instance.Port,
            externalPort = extPorts.TryGetValue(h.Instance.ServiceName, out var ep) ? ep : h.Instance.Port,
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
            entity,                  // юрлицо/владелец (ООО или ФИО ИП) — показываем всегда
            folder = h.Instance.FolderPath,   // папка УТМ
            // Папка УТМ под НАШИМ корнем (…\utms)? Если нет — можно предложить «Собрать в нашу папку».
            inOurFolder = !string.IsNullOrEmpty(h.Instance.FolderPath)
                && Path.GetFullPath(h.Instance.FolderPath).TrimEnd(Path.DirectorySeparatorChar)
                    .StartsWith(Path.GetFullPath(UtmOrchestrator.Core.AppPaths.UtmRoot).TrimEnd(Path.DirectorySeparatorChar),
                                StringComparison.OrdinalIgnoreCase),
            // Точная версия СБОРКИ (напр. 4.27.668) из SPA-бандла УТМ; запасной вариант
            // — версия формата из /api/info/list (4.2.0).
            version = UtmOrchestrator.Core.Diagnostics.UtmBuildVersion.Read(h.Instance.FolderPath) ?? h.Info?.Version,
            formatVersion = h.Info?.Version,  // версия формата (4.2.0)
            firewallOpen = OperatingSystem.IsWindows()
                && UtmOrchestrator.Core.Firewall.FirewallInspector.IsOpen(h.Instance.Port), // порт открыт в брандмауэре?
            // Реальный обмен с ЕГАИС (по логу УТМ): live + сколько назад + счётчики.
            exchange = ex is null ? null : new
            {
                live = ex.Live,
                agoSeconds = ex.SecondsAgo,
                pendingCheques = ex.PendingCheques,
                pendingQueries = ex.PendingQueries,
                pendingAscp = ex.PendingAscp,
            },
            // Очереди документов: incoming = входящие из ЕГАИС (ждут учётную систему),
            // outgoing = исходящие (поданы, ещё не отправлены в ЕГАИС). -1 = неизвестно.
            queue = !h.IsOk ? null : new { incoming = q.Item1, outgoing = q.Item2 },
        });
    }

    return Results.Json(new
    {
        total = health.Count,
        ok,
        faulty = health.Count - ok,
        bringUp = BringUpStatus.Active, // идёт подъём/перепривязка — «не отвечает» это норма
        orchestratorVersion = UtmOrchestrator.Core.AppInfo.Version,
        machine = Environment.MachineName,   // на какой машине работает панель
        lanIp = OperatingSystem.IsWindows() ? UtmOrchestrator.Core.Network.UpnpManager.LanIp() : null,
        maxUtms,                             // лимит числа УТМ на машину
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

// --- Настройки панели (persist). GET — без хэша пароля. ---
app.MapGet("/api/settings", (PanelSettings settings) =>
{
    var s = settings.Current;
    return Results.Json(new
    {
        requireAuth = s.RequireAuth,
        username = s.Username,
        networkAccess = s.NetworkAccess,
        allowedIps = s.AllowedIps,
        hasPassword = s.HasPassword,
    });
});
app.MapPost("/api/settings", (SettingsRequest req, PanelSettings settings) =>
{
    var cur = settings.Current;
    var nd = new PanelSettingsData
    {
        RequireAuth = req.RequireAuth,
        Username = req.Username,
        NetworkAccess = req.NetworkAccess,
        AllowedIps = req.AllowedIps ?? new(),
        PasswordHash = cur.PasswordHash,   // сохраняем существующий, если не меняют
        PasswordSalt = cur.PasswordSalt,
    };
    if (!string.IsNullOrEmpty(req.NewPassword))
    {
        var (h, salt) = PanelPassword.Make(req.NewPassword);
        nd.PasswordHash = h; nd.PasswordSalt = salt;
    }

    // Безопасность: вход требует пароль; доступ по сети требует включённый вход.
    if (nd.RequireAuth && !nd.HasPassword)
        return Results.BadRequest(new { error = "чтобы включить вход, задайте пароль" });
    if (nd.NetworkAccess && !(nd.RequireAuth && nd.HasPassword))
        return Results.BadRequest(new { error = "доступ по сети требует включённого входа с паролем" });

    bool bindChanged = cur.NetworkAccess != nd.NetworkAccess;
    settings.Save(nd);

    // Правило файрвола на порт панели 8090 синхронизируем с доступом по сети.
    if (OperatingSystem.IsWindows())
        UtmOrchestrator.Core.Firewall.FirewallManager.SetPort(8090, nd.NetworkAccess, ReaderOp.FileLog);

    // Смена бинда (localhost ↔ 0.0.0.0) применяется только при перезапуске службы.
    return Results.Ok(new { ok = true, restartRequired = bindChanged });
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

// --- Остановить УТМ (служба). Обмен с ЕГАИС для этой организации прекратится. ---
app.MapPost("/api/utm/stop", (RestartRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Service)) return Results.BadRequest(new { error = "service обязателен" });
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (UtmOrchestrator.Core.Services.ServiceControl.GetState(req.Service) == UtmOrchestrator.Core.Services.ServiceState.NotInstalled)
        return Results.NotFound(new { error = $"служба {req.Service} не найдена" });

    _ = Task.Run(() =>
    {
        try
        {
            UtmOrchestrator.Core.Services.ServiceControl.Stop(req.Service, TimeSpan.FromSeconds(60));
            ReaderOp.FileLog($"stop {req.Service}: остановлен по команде из панели");
        }
        catch (Exception e) { ReaderOp.FileLog($"stop {req.Service}: СБОЙ — {e}"); }
    });
    return Results.Accepted(value: new { ok = true, stopped = req.Service });
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

    // Внешний порт (проброс на роутере) — метаданные, храним даже без управления роутером.
    int extPort = req.ExternalPort is int e and > 0 and <= 65535 ? e : inst.Port;
    if (req.Open)
    {
        inst.ExternalPort = extPort == inst.Port ? null : extPort;
        state.Save(OrchestratorState.DefaultPath);
    }

    UtmOrchestrator.Core.Firewall.FirewallManager.SetPort(inst.Port, req.Open, ReaderOp.FileLog);

    // Роутер трогаем ТОЛЬКО если недавним опросом подтвердили управляемость (UPnP),
    // иначе синхронный COM-вызов повесил бы запрос на таймаут.
    string? router = null;
    if (UtmOrchestrator.Core.Network.UpnpManager.LastManageable == true)
    {
        string? lan = UtmOrchestrator.Core.Network.UpnpManager.LanIp();
        if (req.Open && lan is not null)
            router = UtmOrchestrator.Core.Network.UpnpManager.AddMapping(extPort, inst.Port, lan, $"UTM {inst.ServiceName}", ReaderOp.FileLog)
                ? "проброс на роутере создан" : "не удалось создать проброс на роутере";
        else if (!req.Open)
            UtmOrchestrator.Core.Network.UpnpManager.RemoveMapping(extPort, ReaderOp.FileLog);
    }
    return Results.Ok(new { ok = true, port = inst.Port, externalPort = extPort, open = req.Open, router });
});

// --- Внешний порт УТМ (метаданные проброса) без изменения файрвола ---
app.MapPost("/api/utm/external-port", (ExternalPortRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Service)) return Results.BadRequest(new { error = "service обязателен" });
    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var inst = state.Instances.FirstOrDefault(i =>
        string.Equals(i.ServiceName, req.Service, StringComparison.OrdinalIgnoreCase));
    if (inst is null) return Results.NotFound(new { error = $"УТМ {req.Service} не найден" });
    int ext = req.ExternalPort ?? inst.Port;
    if (ext is <= 0 or > 65535) return Results.BadRequest(new { error = "внешний порт вне диапазона" });
    inst.ExternalPort = ext == inst.Port ? null : ext;
    state.Save(OrchestratorState.DefaultPath);
    return Results.Ok(new { ok = true, service = inst.ServiceName, externalPort = ext });
});

// --- Логи КОНКРЕТНОГО УТМ (его transport_info.log, а не лог оркестратора) ---
app.MapGet("/api/utm/log", async (string service, int? limit, string? level, SerialCache serials, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(service)) return Results.BadRequest(new { error = "service обязателен" });
    var instances = await UtmDiscovery.DiscoverAsync(ct, scanTokens: false, serials);
    var inst = instances.FirstOrDefault(i => string.Equals(i.ServiceName, service, StringComparison.OrdinalIgnoreCase));
    if (inst is null) return Results.NotFound(new { error = $"УТМ {service} не найден" });

    string? path = UtmOrchestrator.Core.Diagnostics.UtmLog.LogPath(inst.FolderPath);
    if (path is null) return Results.Ok(new { service, path = (string?)null, lines = Array.Empty<object>(), note = "лог transport_info.log не найден" });

    var lines = UtmOrchestrator.Core.Diagnostics.UtmLog.Tail(inst.FolderPath, limit ?? 300, level);
    return Results.Json(new
    {
        service,
        path,
        lines = lines.Select(l => new { t = l.Time, level = l.Level, msg = l.Text }),
    });
});

// --- Запрос необработанных накладных (QueryNATTN → ЕГАИС) для ОДНОГО УТМ ---
// Просит ЕГАИС прислать все входящие ТТН, по которым ещё не отправлен акт (ответ и сами
// ТТН придут в /opt/out). Полезно после переноса/сбоя, чтобы дозабрать пропущенное.
app.MapPost("/api/utm/query-unprocessed", async (RestartRequest req, SerialCache serials, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Service)) return Results.BadRequest(new { error = "service обязателен" });
    var instances = await UtmDiscovery.DiscoverAsync(ct, scanTokens: false, serials);
    var inst = instances.FirstOrDefault(i =>
        string.Equals(i.ServiceName, req.Service, StringComparison.OrdinalIgnoreCase));
    if (inst is null) return Results.NotFound(new { error = $"УТМ {req.Service} не найден" });
    if (inst.Port <= 0 || string.IsNullOrEmpty(inst.ExpectedFsrar))
        return Results.BadRequest(new { error = "УТМ не отвечает или неизвестен его ФСРАР" });

    var r = await UtmOrchestrator.Core.Egais.UtmQueries.RequestUnprocessedAsync(inst.Port, inst.ExpectedFsrar!, ct);
    ReaderOp.FileLog($"query-unprocessed {req.Service} (ФСРАР {inst.ExpectedFsrar}): {r.Message}");
    return r.Ok
        ? Results.Ok(new { ok = true, service = req.Service, replyId = r.ReplyId, message = r.Message })
        : Results.BadRequest(new { error = r.Message });
});

// --- Запрос необработанных накладных для ВСЕХ УТМ (сценарий «после переноса/сбоя») ---
app.MapPost("/api/utm/query-unprocessed-all", async (SerialCache serials, CancellationToken ct) =>
{
    var instances = await UtmDiscovery.DiscoverAsync(ct, scanTokens: false, serials);
    var results = new List<object>();
    int accepted = 0;
    foreach (var inst in instances)
    {
        if (inst.Port <= 0 || string.IsNullOrEmpty(inst.ExpectedFsrar))
        {
            results.Add(new { service = inst.ServiceName, ok = false, message = "нет порта/ФСРАР (УТМ не отвечает)" });
            continue;
        }
        var r = await UtmOrchestrator.Core.Egais.UtmQueries.RequestUnprocessedAsync(inst.Port, inst.ExpectedFsrar!, ct);
        if (r.Ok) accepted++;
        results.Add(new { service = inst.ServiceName, ok = r.Ok, message = r.Message });
    }
    ReaderOp.FileLog($"query-unprocessed-all: принято {accepted}/{instances.Count}");
    return Results.Ok(new { ok = true, total = instances.Count, accepted, results });
});

// --- Статус сети: управляем ли роутером (UPnP), внешний IP, CGNAT, текущие пробросы ---
app.MapGet("/api/net/status", () =>
{
    if (!OperatingSystem.IsWindows()) return Results.Ok(new { manageable = false, error = "только Windows" });
    var n = UtmOrchestrator.Core.Network.UpnpManager.CachedProbe(TimeSpan.FromSeconds(60));
    bool cgnat = UtmOrchestrator.Core.Network.UpnpManager.IsPrivateOrCgnat(n.ExternalIp);
    return Results.Ok(new
    {
        manageable = n.Manageable,
        externalIp = n.ExternalIp,
        lanIp = n.LanIp,
        cgnat,           // внешний IP «серый» → проброс бесполезен
        error = n.Error,
        mappings = n.Mappings.Select(m => new
        {
            externalPort = m.ExternalPort, internalPort = m.InternalPort,
            internalClient = m.InternalClient, protocol = m.Protocol,
            enabled = m.Enabled, description = m.Description,
        }),
    });
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
app.MapPost("/api/utm/export", (RestartRequest req, NameStore names) =>
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
    string exportsDir = UtmOrchestrator.Core.AppPaths.Transfer("exports");
    // Кастомная подпись УТМ (имя точки) — кладём в бандл, чтобы не переподписывать на приёмнике.
    string? displayName = names.Get(inst.TokenSerial);

    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        try
        {
            var r = UtmOrchestrator.Core.Transfer.UtmTransfer.Export(inst, allReaders, null, exportsDir, ReaderOp.FileLog, displayName);
            ReaderOp.FileLog($"export {req.Service}: success={r.Success} — {r.Message} {r.BundlePath}");
        }
        catch (Exception e) { ReaderOp.FileLog($"export {req.Service}: СБОЙ — {e}"); }
        finally { ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, service = req.Service });
});

// --- Перенос: ЭКСПОРТ НЕСКОЛЬКИХ УТМ разом (для группового переноса) ---
// Экспорт держит глобальный ReaderOp.Gate, поэтому пакуем ПОСЛЕДОВАТЕЛЬНО в одном фоне:
// один захват gate → цикл по службам (стоп→zip→старт каждой) → отпускаем. Так параллельные
// экспорты не спорят за gate и УТМ по очереди кратко останавливаются, а не все разом.
app.MapPost("/api/utm/export-batch", (BatchServicesRequest req, NameStore names) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    var services = (req.Services ?? new List<string>())
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (services.Count == 0) return Results.BadRequest(new { error = "не выбрано ни одного УТМ" });

    if (!ReaderOp.Gate.Wait(0))
        return Results.Conflict(new { error = "уже идёт операция с ридерами — попробуйте позже" });

    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var allReaders = state.Instances.Select(i => i.ReaderName ?? "").Where(r => r.Length > 0).ToList();
    string exportsDir = UtmOrchestrator.Core.AppPaths.Transfer("exports");
    // Захватываем инстансы+подписи заранее (в фоне state не перечитываем).
    var jobs = services
        .Select(s => state.Instances.FirstOrDefault(i =>
            string.Equals(i.ServiceName, s, StringComparison.OrdinalIgnoreCase)))
        .Where(i => i is not null)
        .Select(i => (Inst: i!, Name: names.Get(i!.TokenSerial)))
        .ToList();
    if (jobs.Count == 0) { ReaderOp.Gate.Release(); return Results.BadRequest(new { error = "выбранные УТМ не найдены" }); }

    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        try
        {
            int ok = 0;
            foreach (var (inst, dn) in jobs)
            {
                try
                {
                    var r = UtmOrchestrator.Core.Transfer.UtmTransfer.Export(inst, allReaders, null, exportsDir, ReaderOp.FileLog, dn);
                    if (r.Success) ok++;
                    ReaderOp.FileLog($"export-batch {inst.ServiceName}: success={r.Success} — {r.Message}");
                }
                catch (Exception e) { ReaderOp.FileLog($"export-batch {inst.ServiceName}: СБОЙ — {e}"); }
            }
            ReaderOp.FileLog($"export-batch: готово, успешно {ok}/{jobs.Count}");
        }
        finally { ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, count = jobs.Count });
});

// --- Список готовых бандлов переноса ---
app.MapGet("/api/exports", () =>
{
    string dir = UtmOrchestrator.Core.AppPaths.Transfer("exports");
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
    string path = Path.Combine(UtmOrchestrator.Core.AppPaths.Transfer("exports"), name);
    if (!File.Exists(path)) return Results.NotFound(new { error = "бандл не найден" });
    return Results.File(path, "application/zip", name);
});

// --- Перенос: ИМПОРТ бандла (сторона-приёмник) ---
// Тело запроса = сам .zip-бандл (raw octet-stream, чтобы не упираться в лимиты multipart
// для больших папок УТМ). Разворачиваем папку + регистрируем службу + пишем в state, НЕ
// поднимая (токен может быть ещё не воткнут). Подпись (имя точки) из манифеста → NameStore.
// Привязка к токену — отдельным шагом «Привязать все токены» (серийный подъём).
app.MapPost("/api/utm/import", async (HttpRequest request, NameStore names, SerialCache serials) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });

    // Снимаем лимит размера тела для этого запроса — бандлы большие (папка УТМ + JRE + база).
    var maxFeat = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (maxFeat is not null && !maxFeat.IsReadOnly) maxFeat.MaxRequestBodySize = null;

    string importsDir = UtmOrchestrator.Core.AppPaths.Transfer("imports");
    Directory.CreateDirectory(importsDir);
    string reqName = request.Query["name"].ToString();
    string safe = Path.GetFileName(reqName);
    if (string.IsNullOrWhiteSpace(safe) || !safe.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        safe = $"UTM-import-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
    string bundlePath = Path.Combine(importsDir, safe);

    // Приём файла (стрим на диск) — вне гейта, чтобы параллельные закачки не ждали друг друга.
    try
    {
        await using var fs = File.Create(bundlePath);
        await request.Body.CopyToAsync(fs);
    }
    catch (Exception e)
    {
        ReaderOp.FileLog($"import: не удалось сохранить бандл — {e.Message}");
        return Results.Problem("не удалось сохранить бандл: " + e.Message);
    }

    // Обработка — под отдельным лёгким гейтом, ИНЛАЙН (клиент грузит бандлы по очереди и
    // видит реальный результат каждого). Импорт не трогает ридеры → ReaderOp.Gate не нужен.
    if (!await importGate.WaitAsync(TimeSpan.FromMinutes(5)))
        return Results.Conflict(new { error = "идёт другой импорт — попробуйте позже" });
    try
    {
        var st = OrchestratorState.Load(OrchestratorState.DefaultPath);
        if (st.Instances.Count >= maxUtms)
            return Results.BadRequest(new { error = $"достигнут лимит {maxUtms} УТМ на этой машине" });

        var r = UtmOrchestrator.Core.Transfer.UtmTransfer.Import(bundlePath, st.Instances, ReaderOp.FileLog);
        if (r.Instance is not null)
        {
            st.Instances.Add(r.Instance);
            st.Save(OrchestratorState.DefaultPath);
            if (!string.IsNullOrEmpty(r.DisplayName) && !string.IsNullOrEmpty(r.TokenSerial))
                names.Set(r.TokenSerial!, r.DisplayName);
            if (!string.IsNullOrEmpty(r.Instance.ExpectedFsrar) && !string.IsNullOrEmpty(r.Instance.TokenSerial))
                serials.Learn(r.Instance.ExpectedFsrar!, r.Instance.TokenSerial!);
            ReaderOp.FileLog($"import: успех — {r.Message} (подпись: {r.DisplayName ?? "—"}, " +
                $"локальный {r.SourcePort}→{r.LocalPort}, внешний {(r.ExternalPort?.ToString() ?? "—")})");
            return Results.Ok(new
            {
                ok = true,
                service = r.Instance.ServiceName,
                name = r.DisplayName,
                sourcePort = r.SourcePort,
                port = r.LocalPort,
                externalPort = r.ExternalPort,
                portChanged = r.SourcePort != r.LocalPort,
            });
        }
        ReaderOp.FileLog($"import: НЕ УДАЛОСЬ — {r.Message}");
        return Results.BadRequest(new { error = r.Message });
    }
    catch (Exception e)
    {
        ReaderOp.FileLog($"import: СБОЙ — {e}");
        return Results.Problem("ошибка импорта: " + e.Message);
    }
    finally { importGate.Release(); }
});

// --- Перенос: ОСМОТР бандла перед импортом (двухфазный импорт с выбором порта) ---
// Тело = .zip (raw). Сохраняем под уникальным handle в imports\, читаем манифест БЕЗ
// разворачивания, возвращаем подпись/серийник/исходный+внешний порт/подсказку локального
// порта. Разворачивание — отдельным шагом /commit с выбранным локальным портом.
app.MapPost("/api/utm/import/inspect", async (HttpRequest request) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    var maxFeat = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (maxFeat is not null && !maxFeat.IsReadOnly) maxFeat.MaxRequestBodySize = null;

    string importsDir = UtmOrchestrator.Core.AppPaths.Transfer("imports");
    Directory.CreateDirectory(importsDir);
    string handle = $"import-{Guid.NewGuid():N}.zip"; // уникальный, чтобы параллельные осмотры не перетёрлись
    string bundlePath = Path.Combine(importsDir, handle);
    try
    {
        await using var fs = File.Create(bundlePath);
        await request.Body.CopyToAsync(fs);
    }
    catch (Exception e) { return Results.Problem("не удалось сохранить бандл: " + e.Message); }

    var st = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var ins = UtmOrchestrator.Core.Transfer.UtmTransfer.Inspect(bundlePath, st.Instances);
    if (!ins.Ok)
    {
        try { File.Delete(bundlePath); } catch { }
        return Results.BadRequest(new { error = ins.Message });
    }
    return Results.Ok(new
    {
        ok = true,
        handle,
        displayName = ins.DisplayName,
        serial = ins.TokenSerial,
        fsrar = ins.Fsrar,
        sourcePort = ins.SourcePort,
        externalPort = ins.ExternalPort,
        suggestedPort = ins.SuggestedPort,
        alreadyBound = ins.AlreadyBound,
    });
});

// --- Перенос: РАЗВЕРНУТЬ осмотренный бандл на выбранный локальный порт ---
app.MapPost("/api/utm/import/commit", async (ImportCommitRequest req, NameStore names, SerialCache serials) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (string.IsNullOrWhiteSpace(req.Handle)) return Results.BadRequest(new { error = "handle обязателен" });
    string safe = Path.GetFileName(req.Handle);
    string bundlePath = Path.Combine(UtmOrchestrator.Core.AppPaths.Transfer("imports"), safe);
    if (!File.Exists(bundlePath)) return Results.NotFound(new { error = "бандл не найден — повторите выбор файла" });

    if (!await importGate.WaitAsync(TimeSpan.FromMinutes(5)))
        return Results.Conflict(new { error = "идёт другой импорт — попробуйте позже" });
    try
    {
        var st = OrchestratorState.Load(OrchestratorState.DefaultPath);
        if (st.Instances.Count >= maxUtms)
            return Results.BadRequest(new { error = $"достигнут лимит {maxUtms} УТМ на этой машине" });

        var r = UtmOrchestrator.Core.Transfer.UtmTransfer.Import(
            bundlePath, st.Instances, ReaderOp.FileLog, req.Port > 0 ? req.Port : null);
        if (r.Instance is not null)
        {
            st.Instances.Add(r.Instance);
            st.Save(OrchestratorState.DefaultPath);
            if (!string.IsNullOrEmpty(r.DisplayName) && !string.IsNullOrEmpty(r.TokenSerial))
                names.Set(r.TokenSerial!, r.DisplayName);
            if (!string.IsNullOrEmpty(r.Instance.ExpectedFsrar) && !string.IsNullOrEmpty(r.Instance.TokenSerial))
                serials.Learn(r.Instance.ExpectedFsrar!, r.Instance.TokenSerial!);
            try { File.Delete(bundlePath); } catch { }
            ReaderOp.FileLog($"import/commit: успех — {r.Message} (подпись {r.DisplayName ?? "—"}, " +
                $"лок {r.SourcePort}->{r.LocalPort}, внеш {(r.ExternalPort?.ToString() ?? "—")})");
            return Results.Ok(new
            {
                ok = true,
                service = r.Instance.ServiceName,
                name = r.DisplayName,
                sourcePort = r.SourcePort,
                port = r.LocalPort,
                externalPort = r.ExternalPort,
                portChanged = r.SourcePort != r.LocalPort,
            });
        }
        ReaderOp.FileLog($"import/commit: НЕ УДАЛОСЬ — {r.Message}");
        return Results.BadRequest(new { error = r.Message });
    }
    catch (Exception e) { ReaderOp.FileLog($"import/commit: СБОЙ — {e}"); return Results.Problem("ошибка импорта: " + e.Message); }
    finally { importGate.Release(); }
});

// --- Серийная привязка ВСЕХ УТМ по токенам (peel-down) ---
// Для после переноса/перестановки токенов: BootBringUp.Apply привязывает каждую службу
// по СЕРИЙНИКУ (не по имени ридера — оно на новой машине другое) и заново вычисляет имена
// ридеров, сохраняя их в state.json. Кратко перезапускает SCardSvr → короткий общий простой.
app.MapPost("/api/utm/rebind-all", () =>
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
            ReaderOp.FileLog($"=== rebind-all (серийная привязка {targets.Count} УТМ по токенам) ===");
            var r = BootBringUp.Apply(targets, ReaderOp.FileLog);
            // Сохраняем фактически наблюдённые имена ридеров — на новой машине они другие,
            // а без корректного ReaderName последующие introduce-перезапуски/лечение сломаются.
            if (r.ReaderBySerial.Count > 0)
            {
                var st = OrchestratorState.Load(OrchestratorState.DefaultPath);
                bool changed = false;
                foreach (var i in st.Instances)
                {
                    if (!string.IsNullOrEmpty(i.TokenSerial)
                        && r.ReaderBySerial.TryGetValue(i.TokenSerial!, out var rn)
                        && !string.Equals(i.ReaderName, rn, StringComparison.OrdinalIgnoreCase))
                    { i.ReaderName = rn; changed = true; }
                }
                if (changed) { st.Save(OrchestratorState.DefaultPath); ReaderOp.FileLog("rebind-all: имена ридеров обновлены в state.json"); }
            }
            ReaderOp.FileLog($"rebind-all: поднято {r.Started.Count}, ошибок {r.Failed.Count}");
        }
        catch (Exception e) { ReaderOp.FileLog($"rebind-all: СБОЙ — {e}"); }
        finally { ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, count = targets.Count });
});

// --- Прямая привязка токена к УТМ по серийнику (без матчинга по ФСРАР) ---
// Нужно при ЗАМЕНЕ токена (новый серийник) и когда ФСРАР в КЭП не пишется — авто-подхват
// «по ФСРАР» тогда не сматчит. Пишем новый серийник этому УТМ в state.json и тут же
// поднимаем ИМЕННО его на этом токене (introduce, session-0-safe). Фон, под общим замком.
app.MapPost("/api/utm/bind", (BindTokenRequest req, SerialCache serials) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (string.IsNullOrWhiteSpace(req.Service) || string.IsNullOrWhiteSpace(req.Serial))
        return Results.BadRequest(new { error = "нужны service и serial токена" });

    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var inst = state.Instances.FirstOrDefault(i =>
        string.Equals(i.ServiceName, req.Service, StringComparison.OrdinalIgnoreCase));
    if (inst is null) return Results.NotFound(new { error = "УТМ не найден в state.json" });

    // Не даём привязать один токен к двум УТМ.
    var other = state.Instances.FirstOrDefault(i =>
        !ReferenceEquals(i, inst) && string.Equals(i.TokenSerial, req.Serial, StringComparison.OrdinalIgnoreCase));
    if (other is not null)
        return Results.Conflict(new { error = $"этот токен уже привязан к {other.ServiceName}" });

    if (!ReaderOp.Gate.Wait(0))
        return Results.Conflict(new { error = "уже идёт операция с ридерами — попробуйте позже" });

    // Пишем привязку СРАЗУ (переживёт даже неудачный подъём — потом можно «Полечить»).
    inst.TokenSerial = req.Serial;
    inst.ReaderName = string.IsNullOrWhiteSpace(req.Reader) ? null : req.Reader;
    if (!string.IsNullOrWhiteSpace(req.Fsrar)) { inst.ExpectedFsrar = req.Fsrar; serials.Learn(req.Fsrar!, req.Serial!); }
    state.Save(OrchestratorState.DefaultPath);

    if (string.IsNullOrWhiteSpace(inst.ReaderName))
    { ReaderOp.Gate.Release(); return Results.BadRequest(new { error = "у токена нет имени ридера — пересканируйте" }); }

    var target = new BootBringUp.Target(inst.ServiceName, inst.Port, req.Serial!, inst.ExpectedFsrar, inst.ReaderName);
    // ВАЖНО: хирургический подъём ТОЛЬКО этого УТМ (introduce его ридера, работающие
    // УТМ держат свои токены — им forget не мешает). НЕ BootBringUp.Apply — тот делает
    // boot-peel-down по ВСЕМ токенам (reset + forget всех) и роняет остальные УТМ.
    var allReaders = state.Instances.Select(i => i.ReaderName ?? "").Where(r => r.Length > 0).ToList();

    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        try
        {
            ReaderOp.FileLog($"=== bind: {inst.ServiceName} → токен {req.Serial} (хирургически, других УТМ не трогаем) ===");
            bool up = BootBringUp.RestartOne(target, allReaders, ReaderOp.FileLog);
            ReaderOp.FileLog($"bind: {(up ? "поднят" : "НЕ поднялся")} {inst.ServiceName}");
        }
        catch (Exception e) { ReaderOp.FileLog($"bind: СБОЙ — {e}"); }
        finally { ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, service = inst.ServiceName, serial = req.Serial });
});

// --- Перенос папки УТМ в НАШ корень (…\utms\utm-N): собрать «не наши» в одно место ---
// Стоп службы → delete.bat (снять procrun) → Move папки → install.bat из нового места
// (пути относительные → служба та же, путь новый) → старт. PC/SC НЕ трогаем: ридер
// остаётся introduce'нут, УТМ переподхватит свой токен на старте. state.json: FolderPath.
static void RelocateInstanceFolder(string service, Action<string> log)
{
    var st = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var inst = st.Instances.FirstOrDefault(i => string.Equals(i.ServiceName, service, StringComparison.OrdinalIgnoreCase));
    if (inst is null || string.IsNullOrWhiteSpace(inst.FolderPath) || !Directory.Exists(inst.FolderPath))
    { log($"relocate {service}: папка не найдена — пропуск"); return; }

    string oldFolder = Path.GetFullPath(inst.FolderPath).TrimEnd(Path.DirectorySeparatorChar);
    string utmRoot = Path.GetFullPath(UtmOrchestrator.Core.AppPaths.UtmRoot).TrimEnd(Path.DirectorySeparatorChar);
    if (oldFolder.StartsWith(utmRoot, StringComparison.OrdinalIgnoreCase)) { log($"relocate {service}: уже в нашей папке"); return; }
    if (!string.Equals(Path.GetPathRoot(oldFolder), Path.GetPathRoot(utmRoot), StringComparison.OrdinalIgnoreCase))
    { log($"relocate {service}: папка на другом диске — перенос не поддержан, пропуск"); return; }

    // Подъём переехавшего УТМ — через introduce-хореографию (RestartOne): forget всех +
    // introduce ТОЛЬКО его ридера → его токен = слот 0 → сядет на СВОЙ токен. Обычный
    // Start после переезда хватал чужой слот 0 → «ошибка ключа RSA». Другие УТМ держат
    // свои токены — forget им не мешает. Если ReaderName нет — обычный старт.
    var target = new BootBringUp.Target(service, inst.Port, inst.TokenSerial ?? "", inst.ExpectedFsrar, inst.ReaderName);
    var allReaders = st.Instances.Select(i => i.ReaderName ?? "").Where(r => r.Length > 0).ToList();
    void BringUp()
    {
        if (!string.IsNullOrEmpty(inst.ReaderName)) BootBringUp.RestartOne(target, allReaders, log);
        else UtmOrchestrator.Core.Services.ServiceControl.Start(service, TimeSpan.FromSeconds(90));
    }

    string newFolder = UtmOrchestrator.Core.AppPaths.NextUtmFolder();
    log($"=== relocate: {service}  {oldFolder} → {newFolder} ===");
    UtmOrchestrator.Core.Services.ServiceControl.Stop(service, TimeSpan.FromSeconds(60));
    System.Threading.Thread.Sleep(700);
    UtmOrchestrator.Core.Install.ProcrunService.Unregister(service, oldFolder, log);
    System.Threading.Thread.Sleep(500);
    try { Directory.Move(oldFolder, newFolder); }
    catch (Exception e)
    {
        // Перенос не удался (занят?) — регистрируем службу обратно на старом месте и поднимаем.
        log($"relocate {service}: Move не удался — {e.Message}; откатываю регистрацию на старую папку");
        UtmOrchestrator.Core.Install.ProcrunService.Register(service, oldFolder, log);
        BringUp();
        return;
    }
    bool reg = UtmOrchestrator.Core.Install.ProcrunService.Register(service, newFolder, log);
    var st2 = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var i2 = st2.Instances.FirstOrDefault(i => string.Equals(i.ServiceName, service, StringComparison.OrdinalIgnoreCase));
    if (i2 is not null) { i2.FolderPath = newFolder; st2.Save(OrchestratorState.DefaultPath); }
    BringUp();
    log($"relocate {service}: {(reg ? "перенесён и поднят на своём токене" : "перенесён, но регистрация под вопросом — проверьте службу")}");
}

app.MapPost("/api/utm/relocate", (RestartRequest req) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (string.IsNullOrWhiteSpace(req.Service)) return Results.BadRequest(new { error = "service обязателен" });
    if (!ReaderOp.Gate.Wait(0)) return Results.Conflict(new { error = "уже идёт операция — попробуйте позже" });
    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        try { RelocateInstanceFolder(req.Service!, ReaderOp.FileLog); }
        catch (Exception e) { ReaderOp.FileLog($"relocate {req.Service}: СБОЙ — {e}"); }
        finally { ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, service = req.Service });
});

app.MapPost("/api/utm/relocate-all", () =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    string utmRoot = Path.GetFullPath(UtmOrchestrator.Core.AppPaths.UtmRoot).TrimEnd(Path.DirectorySeparatorChar);
    var outside = state.Instances
        .Where(i => !string.IsNullOrWhiteSpace(i.FolderPath)
            && !Path.GetFullPath(i.FolderPath).TrimEnd(Path.DirectorySeparatorChar)
                .StartsWith(utmRoot, StringComparison.OrdinalIgnoreCase))
        .Select(i => i.ServiceName).ToList();
    if (outside.Count == 0) return Results.BadRequest(new { error = "все УТМ уже в нашей папке" });
    if (!ReaderOp.Gate.Wait(0)) return Results.Conflict(new { error = "уже идёт операция — попробуйте позже" });
    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        try { foreach (var s in outside) RelocateInstanceFolder(s, ReaderOp.FileLog); }
        catch (Exception e) { ReaderOp.FileLog($"relocate-all: СБОЙ — {e}"); }
        finally { ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, count = outside.Count });
});

// ===== Обновление самих УТМ (transporter+agent) из дистрибутива fsrar.gov.ru =====
string utmUpdWork = Path.Combine(UtmOrchestrator.Core.AppPaths.CacheDir, "utm-update");
string utmUpdApp  = Path.Combine(utmUpdWork, "out", "app");
// service → сколько файлов изменится (dry-run). >0 = обновление доступно. Считаем в /check.
var utmUpdChanges = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
var ruCulture = new System.Globalization.CultureInfo("ru-RU");
string? RuDate(DateTime? d) => d?.ToString("d MMMM yyyy", ruCulture);

// Обновить ОДИН УТМ: стоп → бэкап → apply(шаблон) → introduce-подъём → проверка RSA → откат при сбое.
static bool UpdateOneUtm(string service, string templateApp, Action<string> log)
{
    var st = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var inst = st.Instances.FirstOrDefault(i => string.Equals(i.ServiceName, service, StringComparison.OrdinalIgnoreCase));
    if (inst is null || string.IsNullOrWhiteSpace(inst.FolderPath) || !Directory.Exists(inst.FolderPath))
    { log($"update {service}: папка УТМ не найдена — пропуск"); return false; }

    string folder = inst.FolderPath;
    string backupDir = Path.Combine(UtmOrchestrator.Core.AppPaths.CacheDir, "utm-update-backups",
        service + "_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss"));

    log($"=== update УТМ {service}: {folder} (бэкап: {backupDir}) ===");
    UtmOrchestrator.Core.Services.ServiceControl.Stop(service, TimeSpan.FromSeconds(60));
    System.Threading.Thread.Sleep(800);

    var res = UtmOrchestrator.Core.Install.UtmUpdater.Apply(folder, templateApp, backupDir, dryRun: false, log);
    if (res.Total == 0) log($"update {service}: изменений нет (уже актуально) — просто поднимаю");

    var target = new BootBringUp.Target(service, inst.Port, inst.TokenSerial ?? "", inst.ExpectedFsrar, inst.ReaderName);
    var allReaders = st.Instances.Select(i => i.ReaderName ?? "").Where(r => r.Length > 0).ToList();
    bool up = !string.IsNullOrEmpty(inst.ReaderName)
        ? BootBringUp.RestartOne(target, allReaders, log)
        : UtmOrchestrator.Core.Services.ServiceControl.Start(service, TimeSpan.FromSeconds(90));

    if (!up && res.Total > 0)
    {
        log($"update {service}: НЕ поднялся после апдейта — ОТКАТ из бэкапа");
        UtmOrchestrator.Core.Services.ServiceControl.Stop(service, TimeSpan.FromSeconds(60));
        System.Threading.Thread.Sleep(500);
        UtmOrchestrator.Core.Install.UtmUpdater.Restore(folder, backupDir, res.Added, log);
        up = !string.IsNullOrEmpty(inst.ReaderName)
            ? BootBringUp.RestartOne(target, allReaders, log)
            : UtmOrchestrator.Core.Services.ServiceControl.Start(service, TimeSpan.FromSeconds(90));
        log($"update {service}: после отката {(up ? "поднялся" : "НЕ поднялся — ручной разбор")}");
        return false;
    }
    log($"update {service}: {(up ? "обновлён и поднят" : "поднят, но проверьте")}");
    return up;
}

// Скачать/распаковать дистрибутив УТМ (fsrar, НАПРЯМУЮ). Фон, прогресс в OpProgress. УТМ не трогает.
app.MapPost("/api/utm/update/check", () =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (!ReaderOp.Gate.Wait(0)) return Results.Conflict(new { error = "уже идёт операция — попробуйте позже" });
    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        OpProgress.Start("Проверка обновлений УТМ", 1);
        try
        {
            OpProgress.Update(0, "скачиваю дистрибутив с fsrar.gov.ru (~150 МБ) и распаковываю…", "");
            var app2 = UtmOrchestrator.Core.Install.UtmUpdater.DownloadAndExtract(utmUpdWork, ReaderOp.FileLog);
            ReaderOp.FileLog(app2 is null ? "update/check: не удалось скачать/распаковать" : $"update/check: шаблон готов ({app2})");
            if (app2 is not null)
            {
                // Сравниваем шаблон с каждым установленным УТМ (dry-run) — сколько изменится = есть ли обновление.
                OpProgress.Update(0, "сверяю с установленными УТМ…", "");
                utmUpdChanges.Clear();
                foreach (var i in OrchestratorState.Load(OrchestratorState.DefaultPath).Instances
                             .Where(x => !string.IsNullOrWhiteSpace(x.FolderPath) && Directory.Exists(x.FolderPath)))
                {
                    try
                    {
                        var plan = UtmOrchestrator.Core.Install.UtmUpdater.Apply(
                            i.FolderPath, app2, Path.Combine(Path.GetTempPath(), "utmupd-dry"), dryRun: true, _ => { });
                        utmUpdChanges[i.ServiceName] = plan.Total;
                    }
                    catch (Exception e) { ReaderOp.FileLog($"update/check dry {i.ServiceName}: {e.Message}"); }
                }
            }
        }
        catch (Exception e) { ReaderOp.FileLog($"update/check: СБОЙ — {e}"); }
        finally { OpProgress.Finish(); ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true });
});

app.MapGet("/api/utm/update/status", () =>
{
    bool ready = Directory.Exists(Path.Combine(utmUpdApp, "transporter"));
    string? available = ready ? UtmOrchestrator.Core.Install.UtmUpdater.AvailableVersion(utmUpdApp) : null;
    var availDate = ready ? UtmOrchestrator.Core.Install.UtmUpdater.BuildDate(utmUpdApp) : (DateTime?)null;
    var st = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var utms = st.Instances.Where(i => !string.IsNullOrWhiteSpace(i.FolderPath))
        .Select(i =>
        {
            var instDate = UtmOrchestrator.Core.Install.UtmUpdater.BuildDate(i.FolderPath);
            // «Новее» строго по ДАТЕ сборки — не даунгрейдим (если установленный свежее — не предлагаем).
            bool newer = availDate.HasValue && instDate.HasValue && availDate.Value.Date > instDate.Value.Date;
            int changes = utmUpdChanges.TryGetValue(i.ServiceName, out var ch) ? ch : -1;
            return (object)new
            {
                service = i.ServiceName,
                installedDate = RuDate(instDate),
                newer,
                changes,
            };
        }).ToList();
    return Results.Json(new { ready, available, availableDate = RuDate(availDate), utms });
});

app.MapPost("/api/utm/update", (RestartRequest req) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (string.IsNullOrWhiteSpace(req.Service)) return Results.BadRequest(new { error = "service обязателен" });
    if (!Directory.Exists(Path.Combine(utmUpdApp, "transporter")))
        return Results.BadRequest(new { error = "сначала «Проверить обновления УТМ» (скачать дистрибутив)" });
    if (!ReaderOp.Gate.Wait(0)) return Results.Conflict(new { error = "уже идёт операция — попробуйте позже" });
    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        OpProgress.Start("Обновление УТМ", 1);
        try
        {
            Action<string> plog = m => { ReaderOp.FileLog(m); OpProgress.Update(0, m.Length > 55 ? m.Substring(0, 55) + "…" : m, req.Service!); };
            UpdateOneUtm(req.Service!, utmUpdApp, plog);
        }
        catch (Exception e) { ReaderOp.FileLog($"update {req.Service}: СБОЙ — {e}"); }
        finally { OpProgress.Finish(); ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, service = req.Service });
});

app.MapPost("/api/utm/update-all", () =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (!Directory.Exists(Path.Combine(utmUpdApp, "transporter")))
        return Results.BadRequest(new { error = "сначала «Проверить обновления УТМ»" });
    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var services = state.Instances.Where(i => !string.IsNullOrWhiteSpace(i.FolderPath)).Select(i => i.ServiceName).ToList();
    if (services.Count == 0) return Results.BadRequest(new { error = "нет УТМ" });
    if (!ReaderOp.Gate.Wait(0)) return Results.Conflict(new { error = "уже идёт операция — попробуйте позже" });
    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        OpProgress.Start("Обновление УТМ", services.Count);
        int done = 0;
        try { foreach (var s in services) { OpProgress.Update(done, "обновляю…", s); UpdateOneUtm(s, utmUpdApp, ReaderOp.FileLog); done++; } }
        catch (Exception e) { ReaderOp.FileLog($"update-all: СБОЙ — {e}"); }
        finally { OpProgress.Finish(); ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, count = services.Count });
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

// --- Перезапуск службы оркестратора (из трея, без UAC) ---
// Служба не может корректно перезапустить сама себя изнутри — порождаем ОТДЕЛЬНЫЙ
// процесс (служба = LocalSystem = админ), который переживёт остановку службы. Панель
// пропадёт на ~10-30с; УТМ не трогаются.
app.MapPost("/api/service/restart", () =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "powershell.exe",
            "-NoProfile -WindowStyle Hidden -Command \"Stop-Service UtmOrchestrator -Force; Start-Sleep 2; Start-Service UtmOrchestrator\"")
        { UseShellExecute = false, CreateNoWindow = true });
        ReaderOp.FileLog("service: перезапуск службы по команде из трея");
    }
    catch (Exception e) { return Results.Problem("не удалось запустить перезапуск: " + e.Message); }
    return Results.Accepted(value: new { ok = true });
});

// --- Удалить УТМ: стоп службы + снять регистрацию + убрать из state + правило ФВ (+ файлы) ---
app.MapPost("/api/utm/delete", (DeleteUtmRequest req) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (string.IsNullOrWhiteSpace(req.Service)) return Results.BadRequest(new { error = "service обязателен" });
    if (!ReaderOp.Gate.Wait(0))
        return Results.Conflict(new { error = "идёт операция с ридерами — попробуйте позже" });
    try
    {
        var st = OrchestratorState.Load(OrchestratorState.DefaultPath);
        var inst = st.Instances.FirstOrDefault(i => string.Equals(i.ServiceName, req.Service, StringComparison.OrdinalIgnoreCase));
        if (inst is null) return Results.NotFound(new { error = $"УТМ {req.Service} не найден в конфигурации" });

        ReaderOp.FileLog($"=== удаление УТМ {inst.ServiceName} (папка {inst.FolderPath}, файлы={req.DeleteFiles}) ===");
        try { UtmOrchestrator.Core.Services.ServiceControl.Stop(inst.ServiceName, TimeSpan.FromSeconds(20)); } catch { }
        // Снять службу: сперва штатным delete.bat, иначе sc delete.
        bool unreg = false;
        try { unreg = UtmOrchestrator.Core.Install.ProcrunService.Unregister(inst.ServiceName, inst.FolderPath, ReaderOp.FileLog); } catch { }
        if (!unreg)
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "sc.exe", $"delete {inst.ServiceName}") { UseShellExecute = false, CreateNoWindow = true });
                p!.WaitForExit(10000);
                ReaderOp.FileLog($"delete: sc delete {inst.ServiceName} exit {p.ExitCode}");
            }
            catch (Exception e) { ReaderOp.FileLog($"delete: sc delete не удался: {e.Message}"); }
        }
        if (inst.Port > 0) { try { UtmOrchestrator.Core.Firewall.FirewallManager.DeleteRule(inst.Port, ReaderOp.FileLog); } catch { } }
        st.Instances.RemoveAll(i => string.Equals(i.ServiceName, req.Service, StringComparison.OrdinalIgnoreCase));
        st.Save(OrchestratorState.DefaultPath);
        if (req.DeleteFiles && !string.IsNullOrWhiteSpace(inst.FolderPath) && Directory.Exists(inst.FolderPath))
        {
            try { Directory.Delete(inst.FolderPath, true); ReaderOp.FileLog($"delete: папка удалена {inst.FolderPath}"); }
            catch (Exception e) { ReaderOp.FileLog($"delete: папку удалить не удалось (занята, освободится позже): {e.Message}"); }
        }
        ReaderOp.FileLog($"=== УТМ {inst.ServiceName} удалён ===");
        return Results.Ok(new { ok = true, service = inst.ServiceName });
    }
    catch (Exception e) { ReaderOp.FileLog($"delete: СБОЙ — {e}"); return Results.Problem("ошибка удаления: " + e.Message); }
    finally { ReaderOp.Gate.Release(); }
});

// --- ПОЛНАЯ деинсталляция: снести ВСЕ УТМ + сам оркестратор. Служба не может удалить
// себя изнутри — пишем отдельный uninstall.ps1 во временную папку и запускаем его
// detached (служба = LocalSystem = админ). Он останавливает всё, чистит автозапуск,
// правила ФВ, папки и саму C:\UtmOrchestrator. Требуется явное confirm=true. ⚠ Необратимо. -->
app.MapPost("/api/service/uninstall", (UninstallRequest req) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (req?.Confirm != true) return Results.BadRequest(new { error = "нужно подтверждение (confirm=true)" });
    try
    {
        string appDir = AppContext.BaseDirectory.TrimEnd('\\');
        string ps1 = Path.Combine(Path.GetTempPath(), $"utmo-uninstall-{Guid.NewGuid():N}.ps1");
        // Скрипт запускается ВНЕ C:\UtmOrchestrator, поэтому может удалить и её после стопа службы.
        string script = """
$ErrorActionPreference='SilentlyContinue'
Start-Sleep 2
foreach($s in Get-Service Transport* ){ & sc.exe stop $s.Name | Out-Null }
Start-Sleep 2
Get-Process utm | Stop-Process -Force
foreach($s in Get-Service Transport* ){ & sc.exe delete $s.Name | Out-Null }
& sc.exe stop UtmOrchestrator | Out-Null
Start-Sleep 2
& sc.exe delete UtmOrchestrator | Out-Null
Get-Process *UtmOrchestrator* | Stop-Process -Force
Get-ScheduledTask | ? { $_.TaskName -match 'Utm|Orchestrator' } | Unregister-ScheduledTask -Confirm:$false
foreach($h in 'HKCU:','HKLM:'){ $rk="$h\Software\Microsoft\Windows\CurrentVersion\Run"; $it=Get-Item $rk -EA SilentlyContinue; if($it){ $it.Property | ? { $_ -match 'Utm|Orchestrator' } | % { Remove-ItemProperty $rk -Name $_ } } }
Get-NetFirewallRule | ? { $_.DisplayName -like 'UTM-Orchestrator-*' } | Remove-NetFirewallRule
foreach($f in @(Get-ChildItem 'C:\' -Directory | ? { $_.Name -match '^(UTM|UTM_\d+)$' })){ [System.IO.Directory]::Delete($f.FullName,$true) }
Start-Sleep 1
[System.IO.Directory]::Delete('__APPDIR__',$true)
""";
        script = script.Replace("__APPDIR__", appDir.Replace("'", "''"));
        File.WriteAllText(ps1, script, System.Text.Encoding.UTF8);
        ReaderOp.FileLog($"=== ДЕИНСТАЛЛЯЦИЯ запрошена — запускаю {ps1} (снесёт УТМ + оркестратор) ===");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"")
        { UseShellExecute = false, CreateNoWindow = true });
    }
    catch (Exception e) { return Results.Problem("не удалось запустить деинсталляцию: " + e.Message); }
    return Results.Accepted(value: new { ok = true });
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

// --- Установка НОВОГО УТМ «с нуля» на подключённый токен (session-0, служба=админ) ---
// Развернуть чистый шаблон → порт → регистрация службы (install.bat) → файрвол →
// introduce-привязка → старт → проверка ФСРАР → state.json. Токен должен быть в USB.
app.MapPost("/api/utm/add", (AddUtmRequest req, SerialCache serials) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (string.IsNullOrWhiteSpace(req.Serial) || string.IsNullOrWhiteSpace(req.Reader))
        return Results.BadRequest(new { error = "нужны serial и reader токена" });

    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    if (state.Instances.Any(i => string.Equals(i.TokenSerial, req.Serial, StringComparison.OrdinalIgnoreCase)))
        return Results.Conflict(new { error = "этот токен уже привязан к УТМ" });
    if (state.Instances.Count >= maxUtms)
        return Results.BadRequest(new { error = $"достигнут лимит {maxUtms} УТМ на этой машине" });

    if (!ReaderOp.Gate.Wait(0))
        return Results.Conflict(new { error = "уже идёт операция с ридерами — попробуйте позже" });

    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        try
        {
            // «existing» — для расчёта портов/имён и как источник клон-шаблона. Берём НЕ
            // только из state.json, но и из физически стоящих УТМ (обнаружение): если
            // state.json пуст, но на диске есть C:\UTM — сможем клонировать из него.
            var discovered = UtmDiscovery.DiscoverAsync(default, scanTokens: false, serials)
                .GetAwaiter().GetResult();
            var existing = state.Instances
                .Concat(discovered.Where(d => !state.Instances.Any(s =>
                    string.Equals(s.ServiceName, d.ServiceName, StringComparison.OrdinalIgnoreCase))))
                .ToList();
            var allReaders = existing.Select(i => i.ReaderName ?? "").Where(r => r.Length > 0).ToList();

            var r = UtmOrchestrator.Core.Install.UtmInstaller.AddNew(
                req.Serial!, req.Fsrar, req.Reader!, existing, allReaders, req.Port, ReaderOp.FileLog);
            // Инстанс есть → софт развёрнут и служба зарегистрирована: сохраняем в state.json
            // (даже если не поднялся — тогда дожать можно «Запустить»). null → развернуть не удалось.
            if (r.Instance is not null)
            {
                var st = OrchestratorState.Load(OrchestratorState.DefaultPath);
                st.Instances.Add(r.Instance);
                st.Save(OrchestratorState.DefaultPath);
                if (!string.IsNullOrEmpty(req.Fsrar)) serials.Learn(req.Fsrar!, req.Serial!);
                ReaderOp.FileLog($"add УТМ: {(r.Success ? "успех" : "развёрнут, но не поднялся")} — {r.Message}");
            }
            else ReaderOp.FileLog($"add УТМ: НЕ УДАЛОСЬ — {r.Message}");
        }
        catch (Exception e) { ReaderOp.FileLog($"add УТМ: СБОЙ — {e}"); }
        finally { ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, serial = req.Serial });
});

// --- Установить УТМ на ВСЕ переданные токены разом (последовательно, под общим замком) ---
// Шаблон качается один раз (первый УТМ), дальше из кэша. Уже привязанные токены пропускаем.
app.MapPost("/api/utm/add-all", (AddAllRequest req, SerialCache serials) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    var tokens = (req.Tokens ?? new())
        .Where(t => !string.IsNullOrWhiteSpace(t.Serial) && !string.IsNullOrWhiteSpace(t.Reader))
        .ToList();
    if (tokens.Count == 0) return Results.BadRequest(new { error = "нет токенов с serial и reader" });

    if (!ReaderOp.Gate.Wait(0))
        return Results.Conflict(new { error = "уже идёт операция с ридерами — попробуйте позже" });

    _ = Task.Run(() =>
    {
        using var _ = BringUpStatus.Begin();
        OpProgress.Start("Установка на токены", tokens.Count);
        int done = 0, skipped = 0, failed = 0;
        try
        {
            foreach (var tk in tokens)
            {
                int processed = done + skipped + failed;
                string who = string.IsNullOrWhiteSpace(tk.Reader) ? (tk.Serial ?? "токен") : tk.Reader!;
                OpProgress.Update(processed, "разворачиваю и регистрирую…", who);

                var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
                if (state.Instances.Count >= maxUtms)
                { ReaderOp.FileLog($"add-all: достигнут лимит {maxUtms} УТМ — остальные токены пропущены"); break; }
                if (state.Instances.Any(i => string.Equals(i.TokenSerial, tk.Serial, StringComparison.OrdinalIgnoreCase)))
                { skipped++; ReaderOp.FileLog($"add-all: {tk.Serial} уже привязан — пропуск"); continue; }

                var discovered = UtmDiscovery.DiscoverAsync(default, scanTokens: false, serials).GetAwaiter().GetResult();
                var existing = state.Instances
                    .Concat(discovered.Where(d => !state.Instances.Any(s =>
                        string.Equals(s.ServiceName, d.ServiceName, StringComparison.OrdinalIgnoreCase))))
                    .ToList();
                var allReaders = existing.Select(i => i.ReaderName ?? "").Where(r => r.Length > 0).ToList();

                ReaderOp.FileLog($"add-all: устанавливаю УТМ на {tk.Serial} (ридер {tk.Reader})");
                var r = UtmOrchestrator.Core.Install.UtmInstaller.AddNew(
                    tk.Serial!, tk.Fsrar, tk.Reader!, existing, allReaders, null, ReaderOp.FileLog);
                if (r.Instance is not null)
                {
                    var st = OrchestratorState.Load(OrchestratorState.DefaultPath);
                    st.Instances.Add(r.Instance);
                    st.Save(OrchestratorState.DefaultPath);
                    if (!string.IsNullOrEmpty(tk.Fsrar)) serials.Learn(tk.Fsrar!, tk.Serial!);
                    if (r.Success) { done++; ReaderOp.FileLog($"add-all: {tk.Serial} — успех: {r.Message}"); }
                    else { failed++; ReaderOp.FileLog($"add-all: {tk.Serial} — развёрнут, но не поднялся: {r.Message}"); }
                }
                else { failed++; ReaderOp.FileLog($"add-all: {tk.Serial} — НЕ УДАЛОСЬ: {r.Message}"); }
            }
            OpProgress.Update(done + skipped + failed, "готово", null);
            ReaderOp.FileLog($"add-all: готово — поставлено {done}, пропущено {skipped}, ошибок {failed}");
        }
        catch (Exception e) { ReaderOp.FileLog($"add-all: СБОЙ — {e}"); }
        finally { OpProgress.Finish(); ReaderOp.Gate.Release(); }
    });
    return Results.Accepted(value: new { ok = true, count = tokens.Count });
});

// --- Самообновление оркестратора: статус ---
app.MapGet("/api/update/status", async (CancellationToken ct) =>
{
    var info = await UtmOrchestrator.Core.Update.UpdateChecker.CheckAsync(ct);
    // Остатки прошлой плоской раскладки в корне (если уже bin) — чтобы предложить чистку.
    int flatLeftovers = OperatingSystem.IsWindows() ? UtmOrchestrator.Core.Install.FlatCleanup.Detect().Count : 0;
    return Results.Json(new { current = info.Current, latest = info.Latest, updateAvailable = info.UpdateAvailable, reachable = info.Reachable, reason = info.Error, flatLeftovers });
});

// --- Самообновление: применить (скачать payload → распаковать → detached update.ps1) ---
// update.ps1 остановит службу (наш родитель), заменит файлы и стартанёт заново; он —
// отдельный процесс, поэтому переживёт остановку службы. Панель на ~минуту пропадёт.
// --- Почистить остатки прошлой плоской раскладки (старые exe/dll/wwwroot/… в корне). ---
// Безопасно: только когда уже bin-раскладка; удаляет лишь известный хлам, не трогая
// bin/data/utms/cache/transfer. Занятые файлы пропускает.
app.MapPost("/api/maintenance/cleanup-flat", () =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    if (!UtmOrchestrator.Core.Install.FlatCleanup.IsBinLayout())
        return Results.BadRequest(new { error = "не bin-раскладка — чистить нечего" });
    var (deleted, failed) = UtmOrchestrator.Core.Install.FlatCleanup.Clean(ReaderOp.FileLog);
    ReaderOp.FileLog($"cleanup-flat: удалено {deleted}, ошибок {failed}");
    return Results.Ok(new { ok = true, deleted, failed });
});

app.MapPost("/api/update/apply", async (CancellationToken ct) =>
{
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "только Windows" });
    var info = await UtmOrchestrator.Core.Update.UpdateChecker.CheckAsync(ct);
    if (!info.UpdateAvailable || info.AppUrl is null)
        return Results.BadRequest(new { error = "обновление недоступно" });

    _ = Task.Run(async () =>
    {
        try
        {
            string tmp = Path.Combine(Path.GetTempPath(), "utmo-update-" + Guid.NewGuid().ToString("N"));
            string staging = Path.Combine(tmp, "staging");
            Directory.CreateDirectory(staging);

            // Загрузка релизов идёт через прокси ПОЛЬЗОВАТЕЛЯ (в РФ GitHub часто доступен
            // только так; служба-LocalSystem сама «ходит напрямую» и ловит 403). Резолвер —
            // тот же, что у проверки обновлений.
            var (dlProxy, _) = UtmOrchestrator.Core.Update.GitHubProxy.Resolve();
            using var h = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = dlProxy is not null,
                Proxy = dlProxy,
                DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials,
            }) { Timeout = TimeSpan.FromMinutes(10) };
            async Task Download(string url, string zipName)
            {
                string zip = Path.Combine(tmp, zipName);
                using (var resp = await h.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = File.Create(zip))
                    await src.CopyToAsync(dst);
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, staging, overwriteFiles: true);
            }

            // Наш код качаем всегда; рантайм — только если ключ отличается от установленного.
            ReaderOp.FileLog($"update: качаю app {info.AppUrl}");
            await Download(info.AppUrl!, "app.zip");

            string? localKey = null;
            try { localKey = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "runtime.key")).Trim(); } catch { }
            bool needRuntime = info.RuntimeUrl is not null
                && (localKey is null || !string.Equals(localKey, info.RuntimeKey, StringComparison.OrdinalIgnoreCase));
            if (needRuntime)
            {
                ReaderOp.FileLog($"update: рантайм сменился ({localKey ?? "нет"} → {info.RuntimeKey}), качаю {info.RuntimeUrl}");
                await Download(info.RuntimeUrl!, "runtime.zip");
            }
            else ReaderOp.FileLog($"update: рантайм не менялся ({localKey}) — качаю только app");

            string updatePs1 = Path.Combine(staging, "update.ps1");
            if (!File.Exists(updatePs1)) { ReaderOp.FileLog("update: update.ps1 нет в app.zip"); return; }

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
record FirewallRequest(string Service, bool Open, int? ExternalPort);
record ExternalPortRequest(string Service, int? ExternalPort);
record ChangePortRequest(string Service, int NewPort);
record JobCreateRequest(string Type, string? Params);
record JobResultRequest(string? Result, string? Error);
record AdoptToken(string? Serial, string? Fsrar, string? Reader);
record AdoptRequest(List<AdoptToken>? Tokens);
record LoginRequest(string? Username, string? Password);
record AddUtmRequest(string? Serial, string? Fsrar, string? Reader, int? Port);
record BindTokenRequest(string? Service, string? Serial, string? Fsrar, string? Reader);
record AddAllRequest(List<AdoptToken>? Tokens);
record BatchServicesRequest(List<string>? Services);
record ImportCommitRequest(string? Handle, int Port);
record DeleteUtmRequest(string? Service, bool DeleteFiles);
record UninstallRequest(bool Confirm);
record SettingsRequest(bool RequireAuth, string? Username, bool NetworkAccess, List<string>? AllowedIps, string? NewPassword);

// Кэш статуса обмена по папке УТМ: transport_info.log читаем не чаще раза в ~20с
// (обмен идёт циклами по минутам, чаще незачем; /api/status опрашивают часто).
static class ExchangeCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        (UtmOrchestrator.Core.Diagnostics.UtmLog.Exchange ex, DateTime exp)> _c = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);

    public static UtmOrchestrator.Core.Diagnostics.UtmLog.Exchange? Get(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        if (_c.TryGetValue(folder!, out var v) && v.exp > DateTime.UtcNow) return v.ex;
        var ex = UtmOrchestrator.Core.Diagnostics.UtmLog.ReadExchange(folder);
        _c[folder!] = (ex, DateTime.UtcNow + Ttl);
        return ex;
    }
}

// Кэш счётчиков очередей УТМ (входящие /opt/out, исходящие /opt/in) по порту, ~20с —
// чтобы не бить УТМ по HTTP на каждый опрос статуса.
static class QueueCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int,
        ((int Incoming, int Outgoing) counts, DateTime exp)> _c = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);

    public static (int Incoming, int Outgoing) Get(int port)
    {
        if (port <= 0) return (-1, -1);
        if (_c.TryGetValue(port, out var v) && v.exp > DateTime.UtcNow) return v.counts;
        var counts = UtmOrchestrator.Core.Egais.UtmQueries.QueueCountsAsync(port).GetAwaiter().GetResult();
        _c[port] = (counts, DateTime.UtcNow + Ttl);
        return counts;
    }
}

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
