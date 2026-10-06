using UtmOrchestrator.Core.Alerts;
using UtmOrchestrator.Core.Discovery;
using UtmOrchestrator.Core.Health;

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
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, (HealthVerdict Verdict, DateTime LastAlertUtc)> _state = new();

    public AlertWorker(ILogger<AlertWorker> log, IConfiguration config)
    {
        _log = log;
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
        string machine = Environment.MachineName;

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
                    await Send(settings, BuildProblem(h, machine), svc, ct).ConfigureAwait(false);
                    _state[svc] = (h.Verdict, DateTime.UtcNow);
                    continue;
                }
            }
            else if (h.Verdict == HealthVerdict.Ok
                     && AlertNotifier.KindForVerdict(prev.Verdict) is not null)   // был проблемный → стал Ok
            {
                if (settings.Enabled && settings.OnRecovery)
                    await Send(settings, BuildRecovery(h, machine), svc, ct).ConfigureAwait(false);
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

    private static AlertMessage BuildProblem(InstanceHealth h, string machine)
    {
        string name = h.Instance.ServiceName;
        string fsrar = h.Info?.OwnerId ?? h.Instance.ExpectedFsrar ?? "—";
        string head = h.Verdict switch
        {
            HealthVerdict.NeedRsa => "нужен перевыпуск RSA",
            HealthVerdict.SigningBroken => "подпись падает",
            HealthVerdict.SigningUnconfirmed => "подпись не подтверждена",
            _ => "сбой",
        };
        var body = $"{h.Reason}\nФСРАР {fsrar}, порт {h.Instance.Port}, машина {machine}\nВремя: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        return new AlertMessage(AlertNotifier.KindForVerdict(h.Verdict)!.Value,
            $"УТМ [{machine}] {name}: {head}", body);
    }

    private static AlertMessage BuildRecovery(InstanceHealth h, string machine)
    {
        string name = h.Instance.ServiceName;
        string fsrar = h.Info?.OwnerId ?? h.Instance.ExpectedFsrar ?? "—";
        return new AlertMessage(AlertKind.Recovery,
            $"УТМ [{machine}] {name}: вернулся в норму",
            $"Подпись/обмен восстановлены.\nФСРАР {fsrar}, порт {h.Instance.Port}, машина {machine}\nВремя: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }
}
