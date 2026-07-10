using UtmOrchestrator.Core.Discovery;
using UtmOrchestrator.Core.Health;
using UtmOrchestrator.Core.Services;

namespace UtmOrchestrator.Tray;

public enum OverallStatus { Ok, Warn, Fault, Unknown }

public sealed record StatusSnapshot(
    OverallStatus Overall,
    string Summary,
    ServiceState OrchestratorService,
    IReadOnlyList<InstanceHealth> Utms);

/// <summary>
/// Собирает сводный статус для трея/окна: состояние службы оркестратора +
/// здоровье всех УТМ. Пока трей ходит в Core напрямую; позже — через локальный
/// API службы. Read-only.
/// </summary>
public static class StatusProvider
{
    public const string OrchestratorServiceName = "UtmOrchestrator";

    public static async Task<StatusSnapshot> GetAsync(CancellationToken ct = default)
    {
        ServiceState svc = ServiceControl.GetState(OrchestratorServiceName);

        IReadOnlyList<InstanceHealth> health;
        try
        {
            var instances = await UtmDiscovery.DiscoverAsync(ct);
            health = await new HealthChecker().CheckAsync(instances, ct);
        }
        catch
        {
            health = Array.Empty<InstanceHealth>();
        }

        OverallStatus overall;
        string summary;

        if (health.Count == 0)
        {
            overall = OverallStatus.Unknown;
            summary = "УТМ не найдены";
        }
        else
        {
            int ok = health.Count(h => h.Verdict == HealthVerdict.Ok);
            int faulty = health.Count(h => h.Verdict == HealthVerdict.Faulty);

            if (faulty > 0)
            {
                overall = OverallStatus.Fault;
                summary = $"{ok}/{health.Count} в норме, сбоев: {faulty}";
            }
            else if (ok == health.Count)
            {
                overall = OverallStatus.Ok;
                summary = $"{ok}/{health.Count} в норме";
            }
            else
            {
                overall = OverallStatus.Warn;
                summary = $"{ok}/{health.Count} в норме";
            }
        }

        return new StatusSnapshot(overall, summary, svc, health);
    }
}
