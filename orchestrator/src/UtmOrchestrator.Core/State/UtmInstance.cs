using System.Text.Json.Serialization;

namespace UtmOrchestrator.Core.State;

/// <summary>
/// Один экземпляр УТМ в конфигурации оркестратора: соответствие
/// служба ↔ папка ↔ порт ↔ токен. Первичная привязка токена — по
/// <see cref="TokenSerial"/> (стабильный, есть всегда). <see cref="ExpectedFsrar"/> —
/// ожидаемый ФСРАР (для верификации; может быть неизвестен для пустого токена).
/// </summary>
public sealed class UtmInstance
{
    /// <summary>Веб-порт УТМ (8080, 8081, …) — локальный порт, на котором служба слушает.</summary>
    public int Port { get; set; }

    /// <summary>
    /// Внешний порт, под которым этот УТМ виден «снаружи» (проброс на роутере
    /// WAN:ExternalPort → LAN:Port). Хранится как метаданные независимо от того,
    /// управляем ли мы роутером. null → снаружи тот же порт, что и локальный.
    /// </summary>
    public int? ExternalPort { get; set; }

    /// <summary>Эффективный внешний порт: <see cref="ExternalPort"/> либо локальный <see cref="Port"/>.</summary>
    [JsonIgnore]
    public int EffectiveExternalPort => ExternalPort ?? Port;

    /// <summary>Имя Windows-службы (Transport, Transport2, …).</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>Корневая папка УТМ (например C:\UTM_2).</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Серийник привязанного токена (первичный ключ), hex. Может быть пуст, пока не сопоставлен.</summary>
    public string? TokenSerial { get; set; }

    /// <summary>Ожидаемый ФСРАР-ИД (вторичный сигнал/верификация).</summary>
    public string? ExpectedFsrar { get; set; }

    /// <summary>
    /// Нативное имя PC/SC-ридера/устройства этого токена («Aktiv Rutoken ECP N») —
    /// аналог attrReader в config.ini 2UTM. По нему служба делает SCardIntroduceReader
    /// при позиционировании (write-путь без рестарта SCardSvr). Захватывается при
    /// подъёме/установке (когда токены сканируются) и хранится.
    /// </summary>
    public string? ReaderName { get; set; }
}
