using UtmOrchestrator.Core.Alerts;
using UtmOrchestrator.Core.Diagnostics;
using UtmOrchestrator.Core.Discovery;
using UtmOrchestrator.Core.Health;
using UtmOrchestrator.Core.State;

namespace UtmOrchestrator.Service;

/// <summary>
/// Фоновые уведомления о сбоях: периодически проверяет здоровье и при ПЕРЕХОДЕ УТМ в проблему
/// (или возврате в норму) шлёт во включённые каналы (Email/Telegram/MAX). Настройки и выбор
/// событий — в data\alerts.json (правятся из UI). Антиспам: одно уведомление на новую проблему,
/// повтор не чаще CooldownMinutes. Во время операций с ридерами (bring-up) молчит — это транзиент.
/// </summary>
public sealed class AlertWorker : BackgroundService
{
    private readonly ILogger<AlertWorker> _log;
    private readonly NameStore _names;
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, (HealthVerdict Verdict, DateTime LastAlertUtc)> _state = new();
    private readonly Dictionary<string, DateTime> _stoppedSince = new();
    private static readonly TimeSpan StoppedGrace = TimeSpan.FromMinutes(10);   // дольше — не «операция», а сбой
    private readonly Dictionary<string, DateTime> _unreachableSince = new();
    private static readonly TimeSpan UnreachableGrace = TimeSpan.FromMinutes(10);   // «не отвечает по HTTP» короче — перезапуск/загрузка, не сбой

    // Состояние уведомлений переживает перезапуск службы (обновление оркестратора, ребут): иначе после рестарта
    // «был сбой → стало хорошо» не опознаётся и письмо «вернулся в норму» не приходит.
    private static string StatePath => UtmOrchestrator.Core.AppPaths.Data("alert-state.json");
    private string _savedState = "";

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, PersistedState>>(File.ReadAllText(StatePath));
            if (raw is null) return;
            foreach (var (svc, p) in raw)
                if (Enum.TryParse<HealthVerdict>(p.Verdict, out var v)) _state[svc] = (v, p.LastAlertUtc);
            _savedState = File.ReadAllText(StatePath);
        }
        catch { /* битый файл — начнём с чистого состояния */ }
    }

    private void SaveState()
    {
        try
        {
            var raw = _state.ToDictionary(kv => kv.Key, kv => new PersistedState(kv.Value.Verdict.ToString(), kv.Value.LastAlertUtc));
            string json = System.Text.Json.JsonSerializer.Serialize(raw);
            if (json == _savedState) return;               // пишем только при изменении
            File.WriteAllText(StatePath, json);
            _savedState = json;
        }
        catch { }
    }

    private sealed record PersistedState(string Verdict, DateTime LastAlertUtc);

    public AlertWorker(ILogger<AlertWorker> log, IConfiguration config, NameStore names)
    {
        _log = log;
        _names = names;
        int sec = config.GetValue("AlertCheckIntervalSeconds", 60);
        _interval = TimeSpan.FromSeconds(Math.Max(20, sec));
        LoadState();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var checker = new HealthChecker();
        while (!ct.IsCancellationRequested)
        {
            try { await Tick(checker, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { _log.LogError(e, "AlertWorker: ошибка цикла"); }
            try { await Task.Delay(_interval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private DateTime _lastProbeUtc = DateTime.MinValue;
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromMinutes(10);

    private async Task Tick(HealthChecker checker, CancellationToken ct)
    {
        var settings = AlertSettings.Load();              // перечитываем каждый цикл — правки из UI сразу в силе

        // Фоновая проверка доступности каналов (~раз в 10 мин, только при включённых уведомлениях):
        // чтобы ЗАРАНЕЕ видеть в панели, дойдёт ли уведомление (напр. Telegram через прокси недоступен).
        if (settings.Enabled && DateTime.UtcNow - _lastProbeUtc >= ProbeEvery)
        {
            _lastProbeUtc = DateTime.UtcNow;
            try
            {
                var res = await AlertNotifier.CheckAsync(settings, ct).ConfigureAwait(false);
                foreach (var r in res)
                    if (!r.Ok) _log.LogWarning("Канал уведомлений [{Ch}] недоступен: {Detail}", r.Channel, r.Detail);
            }
            catch (Exception e) { _log.LogWarning(e, "AlertWorker: проверка доступности каналов"); }
        }

        if (BringUpStatus.Active) return;                 // идёт операция — не шлём транзиентные сбои

        var instances = await UtmDiscovery.DiscoverAsync(ct).ConfigureAwait(false);
        // Самолечение отравлённого transport.properties (Log4Shell-проба в списке IP роняет УТМ на старте).
        // Дёшево: быстрый выход, если «${» в конфиге нет. Лечим ДО проверки здоровья.
        var healed = new List<(UtmInstance Inst, UtmConfigSanitizer.Result Res)>();
        foreach (var inst in instances)
        {
            var r = UtmConfigSanitizer.SanitizeFolder(inst.FolderPath);
            if (r is null) continue;
            healed.Add((inst, r));
            _log.LogWarning("Самолечение {Svc}: из transport.properties удалено {N} вредоносных элементов IP-списка ({Items})",
                inst.ServiceName, r.Removed, string.Join(" | ", r.Items.Select(i => i.Length > 60 ? i[..60] + "…" : i)));
            ReaderOp.FileLog($"самолечение {inst.ServiceName}: transport.properties — сброшен журнал web.server.attempt.ip, удалено нетипичных элементов: {r.Removed} (бэкап .bak_poison)");
            // служба, упавшая из-за этого, сама не поднимется — поднимаем (старт и так чистит конфиг)
            if (OperatingSystem.IsWindows()
                && UtmOrchestrator.Core.Services.ServiceControl.GetState(inst.ServiceName) == UtmOrchestrator.Core.Services.ServiceState.Stopped)
            {
                var name = inst.ServiceName;
                _ = Task.Run(() => { try { UtmOrchestrator.Core.Services.ServiceControl.Start(name, TimeSpan.FromSeconds(90)); } catch { } });
            }
        }

        var health = await checker.CheckAsync(instances, ct).ConfigureAwait(false);
        var ctx = AlertContext.Build(_names);

        foreach (var (inst, res) in healed)
            if (settings.Enabled && settings.OnRecovery)
                await Send(settings, ctx.SelfHealed(inst, res), inst.ServiceName, ct).ConfigureAwait(false);

        foreach (var h in health)
        {
            string svc = h.Instance.ServiceName;
            var prev = _state.TryGetValue(svc, out var st) ? st : (Verdict: HealthVerdict.Unknown, LastAlertUtc: DateTime.MinValue);
            var kind = AlertNotifier.KindForVerdict(h.Verdict);
            AlertMessage? custom = null;
            // Остановленная служба вне операций оркестратора дольше льготного окна — это сбой (служба падает при старте,
            // упал токен/Java и т.п.). Раньше Stopped считалось «намеренно» и молчало. Идёт по событию «сбой».
            if (h.Verdict == HealthVerdict.Stopped)
            {
                if (!_stoppedSince.TryGetValue(svc, out var since)) _stoppedSince[svc] = since = DateTime.UtcNow;
                if (DateTime.UtcNow - since >= StoppedGrace) { kind = AlertKind.Faulty; custom = ctx.Stopped(h, since); }
            }
            else _stoppedSince.Remove(svc);

            // «Не отвечает по HTTP» (Info == null): при перезапуске/подъёме УТМ грузится 1–2 мин — это не сбой.
            // Уведомляем, только если молчит дольше льготного окна.
            if (h.Verdict == HealthVerdict.Faulty && h.Info is null)
            {
                if (!_unreachableSince.TryGetValue(svc, out var us)) _unreachableSince[svc] = us = DateTime.UtcNow;
                if (DateTime.UtcNow - us < UnreachableGrace) kind = null;
            }
            else _unreachableSince.Remove(svc);

            if (kind is { } k)
            {
                bool isNew = prev.Verdict != h.Verdict;    // появилась проблема / сменился вердикт
                bool cooled = DateTime.UtcNow - prev.LastAlertUtc >= TimeSpan.FromMinutes(Math.Max(1, settings.CooldownMinutes));
                if (settings.Enabled && settings.WantsKind(k) && (isNew || cooled))
                {
                    await Send(settings, await WithDiag(custom ?? ctx.Problem(h), h, ct), svc, ct).ConfigureAwait(false);
                    _state[svc] = (h.Verdict, DateTime.UtcNow);
                    continue;
                }
            }
            else if (h.Verdict == HealthVerdict.Ok
                     && prev.LastAlertUtc != DateTime.MinValue)   // восстановление — только если о проблеме уже уведомляли   // был проблемный → стал Ok
            {
                if (settings.Enabled && settings.OnRecovery)
                    await Send(settings, ctx.Recovery(h), svc, ct).ConfigureAwait(false);
            }

            _state[svc] = (h.Verdict, h.Verdict == HealthVerdict.Ok ? DateTime.MinValue   // норма — сбрасываем: следующая проблема уведомит сразу
                : _state.TryGetValue(svc, out var s2) ? s2.LastAlertUtc : DateTime.MinValue);
        }
        SaveState();
    }

    /// <summary>Прикладывает к сообщению о сбое диагностический zip этого УТМ (логи, конфиг без секретов, состояние службы).</summary>
    private async Task<AlertMessage> WithDiag(AlertMessage msg, InstanceHealth h, CancellationToken ct)
    {
        try
        {
            var i = h.Instance;
            var bytes = await Task.Run(() => DiagBundle.Build(i.ServiceName, i.FolderPath, i.Port, i.ExpectedFsrar, i.TokenSerial), ct).ConfigureAwait(false);
            return msg with { Attachments = new[] { new AlertAttachment(DiagBundle.FileName(i.ServiceName), bytes) } };
        }
        catch (Exception e) { _log.LogWarning(e, "Диагностика для вложения не собрана ({Svc})", h.Instance.ServiceName); return msg; }
    }

    private async Task Send(AlertSettings settings, AlertMessage msg, string svc, CancellationToken ct)
    {
        var res = await AlertNotifier.SendAsync(settings, msg, ct).ConfigureAwait(false);
        foreach (var kv in res)
        {
            if (kv.Value is null) _log.LogInformation("Уведомление [{Ch}] отправлено: {Svc}", kv.Key, svc);
            else _log.LogWarning("Уведомление [{Ch}] не отправлено ({Svc}): {Err}", kv.Key, svc, kv.Value);
        }
    }
}

/// <summary>Контекст для сборки сообщений: человеческие имена УТМ, внешние порты, адрес панели.
/// Общий для фонового воркера и тестовой кнопки — чтобы письма выглядели одинаково.</summary>
public sealed class AlertContext
{
    private readonly NameStore _names;
    private readonly Dictionary<string, int> _extPorts;
    public string Machine { get; } = Environment.MachineName;
    public string? LanIp { get; }
    public string? PanelUrl => LanIp is null ? null : $"http://{LanIp}:8090/";

    private AlertContext(NameStore names, Dictionary<string, int> extPorts, string? lanIp)
    {
        _names = names; _extPorts = extPorts; LanIp = lanIp;
    }

    public static AlertContext Build(NameStore names)
    {
        var ext = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var stored = OrchestratorState.Load(OrchestratorState.DefaultPath);
            foreach (var i in stored.Instances.Where(i => i.ExternalPort.HasValue))
                ext.TryAdd(i.ServiceName, i.ExternalPort!.Value);
        }
        catch { /* нет state.json — ссылки на УТМ будут по локальному порту */ }
        string? lan = null;
        try { if (OperatingSystem.IsWindows()) lan = UtmOrchestrator.Core.Network.UpnpManager.LanIp(); } catch { }
        return new AlertContext(names, ext, lan);
    }

    private string UtmName(InstanceHealth h) => _names.Get(h.Instance.TokenSerial) ?? h.Instance.ServiceName;

    private string? UtmUrl2(UtmInstance i) => LanIp is null ? null : $"http://{LanIp}:{(_extPorts.TryGetValue(i.ServiceName, out var ep) ? ep : i.Port)}/";

    private string? UtmUrl(InstanceHealth h)
    {
        if (LanIp is null) return null;
        int port = _extPorts.TryGetValue(h.Instance.ServiceName, out var ep) ? ep : h.Instance.Port;
        return $"http://{LanIp}:{port}/";
    }

    public AlertMessage Problem(InstanceHealth h)
    {
        var kind = AlertNotifier.KindForVerdict(h.Verdict) ?? AlertKind.Faulty;
        string name = UtmName(h);
        string fsrar = h.Info?.OwnerId ?? h.Instance.ExpectedFsrar ?? "—";
        string reason = h.Reason ?? AlertHints.Label(kind);
        return new AlertMessage(kind,
            $"{AlertHints.Emoji(kind)} УТМ «{name}» [{Machine}]: {AlertHints.Label(kind).ToLowerInvariant()}",
            $"{reason}\nФСРАР {fsrar}, порт {h.Instance.Port}, машина {Machine}")
        {
            Machine = Machine, Utm = name, Service = h.Instance.ServiceName, Fsrar = fsrar, Port = h.Instance.Port,
            Reason = reason, Hint = AlertHints.Hint(kind), PanelUrl = PanelUrl, UtmUrl = UtmUrl(h),
        };
    }

    /// <summary>Причина падения из daemon-error.log самого УТМ (последняя строка с ошибкой) — чтобы письмо сразу говорило, что сломалось.</summary>
    private static string? LogCause(string? folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            string p = Path.Combine(folder, "transporter", "l", "daemon-error.log");
            if (!File.Exists(p)) return null;
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - 64 * 1024); fs.Seek(start, SeekOrigin.Begin);
            string tail = new StreamReader(fs).ReadToEnd();
            var m = System.Text.RegularExpressions.Regex.Matches(tail, @"Could not resolve placeholder '([^']+)' in value ""([^""]{0,160})");
            if (m.Count > 0)
                return $"в конфиге есть неразрешимая подстановка «${{{m[^1].Groups[1].Value}}}» в значении «{m[^1].Groups[2].Value}…» (отравленный список IP, пробы Log4Shell)";
            var line = tail.Split('\n').LastOrDefault(l => l.Contains("Exception") || l.Contains("ERROR") || l.Contains("Error"));
            return line is null ? null : (line.Trim().Length > 220 ? line.Trim()[..220] + "…" : line.Trim());
        }
        catch { return null; }
    }

    /// <summary>УТМ остановлен дольше льготного окна и никто его не поднял.</summary>
    public AlertMessage Stopped(InstanceHealth h, DateTime sinceUtc)
    {
        string name = UtmName(h);
        string fsrar = h.Info?.OwnerId ?? h.Instance.ExpectedFsrar ?? "—";
        int mins = (int)(DateTime.UtcNow - sinceUtc).TotalMinutes;
        string reason = $"Служба не работает уже {mins} мин. Если она стартует и сразу останавливается — это падение при запуске (токен, Java, БД, порт).";
        string? cause = LogCause(h.Instance.FolderPath);
        if (cause is not null) reason += "\nПричина из лога УТМ: " + cause + ".";
        return new AlertMessage(AlertKind.Faulty,
            $"🔴 УТМ «{name}» [{Machine}]: остановлен",
            reason)
        {
            Machine = Machine, Utm = name, Service = h.Instance.ServiceName, Fsrar = fsrar, Port = h.Instance.Port,
            Reason = reason,
            Hint = "Откройте карточку УТМ в панели: «Поднять» / «Привязать токен». Если служба снова падает — нажмите «Диагностика (zip)» и пришлите архив. Если остановка намеренная, можно игнорировать.",
            PanelUrl = PanelUrl, UtmUrl = UtmUrl(h),
        };
    }

    /// <summary>Оркестратор сам вылечил конфиг УТМ (убрал Log4Shell-пробу из списка IP).</summary>
    public AlertMessage SelfHealed(UtmInstance inst, UtmConfigSanitizer.Result r)
    {
        string name = _names.Get(inst.TokenSerial) ?? inst.ServiceName;
        string reason = $"В transport.properties сброшен журнал web.server.attempt.ip (в нём было {r.Removed} нетипичных элементов — пробы (Log4Shell: «${{jndi:ldap://…}}»). " +
                        "Из-за них УТМ падал на старте. Копия прежнего файла: transport.properties.bak_poison. Остановленная служба запущена.";
        return new AlertMessage(AlertKind.Recovery, $"🟢 УТМ «{name}» [{Machine}]: конфиг вылечен автоматически", reason)
        {
            Machine = Machine, Utm = name, Service = inst.ServiceName, Fsrar = inst.ExpectedFsrar, Port = inst.Port, Reason = reason,
            Hint = "Порт УТМ открыт в интернет, и его сканируют — список может отравиться снова (оркестратор вычистит). Лучше закрыть внешний порт на роутере или ограничить адресами ФСРАР и учётной системы.",
            PanelUrl = PanelUrl, UtmUrl = UtmUrl2(inst),
        };
    }

    public AlertMessage Recovery(InstanceHealth h)
    {
        string name = UtmName(h);
        string fsrar = h.Info?.OwnerId ?? h.Instance.ExpectedFsrar ?? "—";
        return new AlertMessage(AlertKind.Recovery,
            $"🟢 УТМ «{name}» [{Machine}]: вернулся в норму",
            $"Подпись и обмен восстановлены.\nФСРАР {fsrar}, порт {h.Instance.Port}, машина {Machine}")
        {
            Machine = Machine, Utm = name, Service = h.Instance.ServiceName, Fsrar = fsrar, Port = h.Instance.Port,
            Reason = "Подпись и обмен восстановлены — УТМ снова отвечает и подписывает документы.",
            Hint = AlertHints.Hint(AlertKind.Recovery), PanelUrl = PanelUrl, UtmUrl = UtmUrl(h),
        };
    }

    /// <summary>Тестовое сообщение из настроек: показывает, какие события и каналы включены.</summary>
    public AlertMessage Test(AlertSettings s, int utmTotal, int utmOk)
    {
        var events = new List<string>();
        if (s.OnFaulty) events.Add("сбой УТМ");
        if (s.OnNeedRsa) events.Add("нужен перевыпуск RSA");
        if (s.OnSigningBroken) events.Add("подпись падает");
        if (s.OnSigningUnconfirmed) events.Add("подпись не подтверждена");
        if (s.OnRecovery) events.Add("возврат в норму");
        var channels = new List<string>();
        if (s.Email.Enabled) channels.Add("Email");
        if (s.Telegram.Enabled) channels.Add("Telegram");
        if (s.Max.Enabled) channels.Add("MAX");

        return new AlertMessage(AlertKind.Test,
            $"🔵 УТМ:Оркестратор [{Machine}]: тест уведомлений",
            "Это тестовое сообщение. Если вы его видите — канал настроен верно.")
        {
            Machine = Machine,
            Hint = AlertHints.Hint(AlertKind.Test),
            PanelUrl = PanelUrl,
            Extra = new[]
            {
                ("УТМ на машине", utmTotal == 0 ? "—" : $"{utmOk} из {utmTotal} в норме"),
                ("Уведомления", s.Enabled ? "включены" : "ВЫКЛЮЧЕНЫ (включите в настройках)"),
                ("События", events.Count == 0 ? "ни одно не выбрано" : string.Join(", ", events)),
                ("Каналы", channels.Count == 0 ? "ни один не включён" : string.Join(", ", channels)),
                ("Пауза между повторами", $"{s.CooldownMinutes} мин"),
            },
        };
    }
}
