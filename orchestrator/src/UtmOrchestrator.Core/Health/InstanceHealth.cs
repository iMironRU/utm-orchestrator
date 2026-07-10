using UtmOrchestrator.Core.Diagnostics;
using UtmOrchestrator.Core.Services;
using UtmOrchestrator.Core.State;

namespace UtmOrchestrator.Core.Health;

public enum HealthVerdict
{
    /// <summary>Работает и привязан к своему токену, обмен возможен.</summary>
    Ok,
    /// <summary>Намеренно остановлен.</summary>
    Stopped,
    /// <summary>Сбой — см. <see cref="InstanceHealth.Reason"/>.</summary>
    Faulty,
    /// <summary>Состояние не определено.</summary>
    Unknown,
}

/// <summary>Здоровье одного УТМ на момент проверки. Причина сбоя — человеко-понятная.</summary>
public sealed record InstanceHealth(
    UtmInstance Instance,
    ServiceState ServiceState,
    UtmInfo? Info,
    HealthVerdict Verdict,
    string? Reason)
{
    public bool IsOk => Verdict == HealthVerdict.Ok;
}
