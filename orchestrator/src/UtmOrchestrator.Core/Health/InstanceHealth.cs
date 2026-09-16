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
    /// <summary>Токен сел, ГОСТ читается, но RSA невалиден — типовое состояние после
    /// перевыпуска КЭП. НЕ поломка: RSA перевыпускается через сам УТМ. Обмен пока невозможен.</summary>
    NeedRsa,
    /// <summary>УТМ Running и /api/info/list в порядке (RSA/ГОСТ по отдельности valid, свой ФСРАР),
    /// но по access_log он реально НЕ подписывает исходящие (POST /opt/in/* → 500). Невидимо в
    /// самом УТМ. Причина/действие — в <see cref="InstanceHealth.Signing"/> (напр. gost-isolate).</summary>
    SigningBroken,
    /// <summary>Состояние не определено.</summary>
    Unknown,
}

/// <summary>Здоровье одного УТМ на момент проверки. Причина сбоя — человеко-понятная.
/// <paramref name="Signing"/> — сигнал реальной подписи по access_log (может быть null,
/// если инстанс не Running или лог недоступен).</summary>
public sealed record InstanceHealth(
    UtmInstance Instance,
    ServiceState ServiceState,
    UtmInfo? Info,
    HealthVerdict Verdict,
    string? Reason,
    SigningHealth? Signing = null)
{
    public bool IsOk => Verdict == HealthVerdict.Ok;
}
