namespace UtmOrchestrator.Core.State;

/// <summary>
/// Один экземпляр УТМ в конфигурации оркестратора: соответствие
/// служба ↔ папка ↔ порт ↔ токен. Первичная привязка токена — по
/// <see cref="TokenSerial"/> (стабильный, есть всегда). <see cref="ExpectedFsrar"/> —
/// ожидаемый ФСРАР (для верификации; может быть неизвестен для пустого токена).
/// </summary>
public sealed class UtmInstance
{
    /// <summary>Веб-порт УТМ (8080, 8081, …).</summary>
    public int Port { get; set; }

    /// <summary>Имя Windows-службы (Transport, Transport2, …).</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>Корневая папка УТМ (например C:\UTM_2).</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Серийник привязанного токена (первичный ключ), hex. Может быть пуст, пока не сопоставлен.</summary>
    public string? TokenSerial { get; set; }

    /// <summary>Ожидаемый ФСРАР-ИД (вторичный сигнал/верификация).</summary>
    public string? ExpectedFsrar { get; set; }
}
