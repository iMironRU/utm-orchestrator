namespace UtmOrchestrator.Core.Diagnostics;

/// <summary>
/// Выжимка из GET /api/info/list одного УТМ. Авторитетный сигнал привязки:
/// <see cref="OwnerId"/>/<see cref="DbOwnerId"/> (какой ФСРАР записан в БД) и
/// <see cref="RsaError"/> (null = RSA-путь в порядке).
/// </summary>
public sealed record UtmInfo(
    string? Version,
    string? OwnerId,
    string? DbOwnerId,
    string? RsaError,
    bool GostValid,
    bool RsaValid)
{
    public bool RsaOk => string.IsNullOrEmpty(RsaError);
}
