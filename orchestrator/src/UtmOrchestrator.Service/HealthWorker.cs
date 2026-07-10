using UtmOrchestrator.Core.Discovery;
using UtmOrchestrator.Core.Health;

namespace UtmOrchestrator.Service;

/// <summary>
/// Служба в режиме НАБЛЮДЕНИЯ: периодически проверяет здоровье всех УТМ и пишет
/// в лог. Ничего не меняет (безопасно для боевой машины). Позже добавим boot-
/// bring-up и точечное восстановление отдельными, явно включаемыми режимами.
/// </summary>
public sealed class HealthWorker : BackgroundService
{
    private readonly ILogger<HealthWorker> _log;
    private readonly TimeSpan _interval;

    public HealthWorker(ILogger<HealthWorker> log, IConfiguration config)
    {
        _log = log;
        // По умолчанию раз в 1.5 часа (как в беклоге). Для обкатки можно задать
        // короче через конфиг: "HealthCheckIntervalSeconds".
        int seconds = config.GetValue("HealthCheckIntervalSeconds", 5400);
        _interval = TimeSpan.FromSeconds(Math.Max(5, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("UtmOrchestrator (наблюдение) запущен. Интервал проверки: {Interval}", _interval);
        var checker = new HealthChecker();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var instances = await UtmDiscovery.DiscoverAsync(stoppingToken);
                var health = await checker.CheckAsync(instances, stoppingToken);

                int ok = 0;
                foreach (var h in health) if (h.IsOk) ok++;
                _log.LogInformation("Проверка здоровья: {Ok}/{Total} в норме", ok, health.Count);

                foreach (var h in health)
                {
                    if (h.IsOk)
                        _log.LogInformation("  {Svc} :{Port} — OK ({Fsrar})",
                            h.Instance.ServiceName, h.Instance.Port, h.Info?.OwnerId);
                    else
                        _log.LogWarning("  {Svc} :{Port} — {Verdict}: {Reason}",
                            h.Instance.ServiceName, h.Instance.Port, h.Verdict, h.Reason);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _log.LogError(e, "Ошибка проверки здоровья");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("UtmOrchestrator (наблюдение) остановлен.");
    }
}
