using UtmOrchestrator.Core.Alerts;
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

    public AlertWorker(ILogger<AlertWorker> log, IConfiguration config, NameStore names)
    {
        _log = log;
        _names = names;
        int sec = config.GetValue("AlertCheckIntervalSeconds", 60);
        _interval = TimeSpan.FromSeconds(Math.Max(20, sec));
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
        var health = await checker.CheckAsync(instances, ct).ConfigureAwait(false);
        var ctx = AlertContext.Build(_names);

        foreach (var h in health)
        {
            string svc = h.Instance.ServiceName;
            var prev = _state.TryGetValue(svc, out var st) ? st : (Verdict: HealthVerdict.Unknown, LastAlertUtc: DateTime.MinValue);
            var kind = AlertNotifier.KindForVerdict(h.Verdict);

            if (kind is { } k)
            {
                bool isNew = prev.Verdict != h.Verdict;    // появилась проблема / сменился вердикт
                bool cooled = DateTime.UtcNow - prev.LastAlertUtc >= TimeSpan.FromMinutes(Math.Max(1, settings.CooldownMinutes));
                if (settings.Enabled && settings.WantsKind(k) && (isNew || cooled))
                {
                    await Send(settings, ctx.Problem(h), svc, ct).ConfigureAwait(false);
                    _state[svc] = (h.Verdict, DateTime.UtcNow);
                    continue;
                }
            }
            else if (h.Verdict == HealthVerdict.Ok
                     && AlertNotifier.KindForVerdict(prev.Verdict) is not null)   // был проблемный → стал Ok
            {
                if (settings.Enabled && settings.OnRecovery)
                    await Send(settings, ctx.Recovery(h), svc, ct).ConfigureAwait(false);
            }

            _state[svc] = (h.Verdict, _state.TryGetValue(svc, out var s2) ? s2.LastAlertUtc : DateTime.MinValue);
        }
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
