using System.Runtime.Versioning;
using UtmOrchestrator.Core.Diagnostics;
using UtmOrchestrator.Core.Services;
using UtmOrchestrator.Core.State;

namespace UtmOrchestrator.Core.Health;

/// <summary>
/// Read-only оценка здоровья набора УТМ: состояние службы + /api/info/list →
/// вердикт (OK / Stopped / Faulty + причина). Ничего не меняет. Используется и
/// службой-наблюдателем, и панелью.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HealthChecker
{
    private readonly TimeSpan _httpTimeout;

    public HealthChecker(TimeSpan? httpTimeout = null)
        => _httpTimeout = httpTimeout ?? TimeSpan.FromSeconds(6);

    public async Task<IReadOnlyList<InstanceHealth>> CheckAsync(
        IEnumerable<UtmInstance> instances, CancellationToken ct = default)
    {
        var result = new List<InstanceHealth>();
        using var http = new UtmHttpClient(_httpTimeout);

        foreach (var inst in instances)
        {
            var state = ServiceControl.GetState(inst.ServiceName);
            UtmInfo? info = null;
            HealthVerdict verdict;
            string? reason;

            switch (state)
            {
                case ServiceState.NotInstalled:
                    verdict = HealthVerdict.Faulty;
                    reason = "служба не установлена";
                    break;

                case ServiceState.Stopped:
                    verdict = HealthVerdict.Stopped;
                    reason = "служба остановлена";
                    break;

                case ServiceState.StartPending:
                case ServiceState.StopPending:
                    verdict = HealthVerdict.Unknown;
                    reason = "служба меняет состояние";
                    break;

                default: // Running / Other
                    info = inst.Port > 0 ? await http.GetInfoAsync(inst.Port, ct).ConfigureAwait(false) : null;
                    (verdict, reason) = Evaluate(inst, info);
                    break;
            }

            result.Add(new InstanceHealth(inst, state, info, verdict, reason));
        }

        return result;
    }

    private static (HealthVerdict, string?) Evaluate(UtmInstance inst, UtmInfo? info)
    {
        if (info is null)
            return (HealthVerdict.Faulty, "не отвечает по HTTP (ещё грузится или завис)");

        if (!info.RsaOk)
            return (HealthVerdict.Faulty, "ошибка ключа RSA (не тот токен на слоте 0 при старте?)");

        if (!string.IsNullOrEmpty(inst.ExpectedFsrar)
            && !string.Equals(info.OwnerId, inst.ExpectedFsrar, StringComparison.OrdinalIgnoreCase))
        {
            return (HealthVerdict.Faulty,
                $"привязан не тот токен: ожидался {inst.ExpectedFsrar}, читается {info.OwnerId ?? "неизвестно"}");
        }

        if (!info.GostValid)
            return (HealthVerdict.Faulty, "ГОСТ-сертификат недоступен/невалиден");

        return (HealthVerdict.Ok, null);
    }
}
