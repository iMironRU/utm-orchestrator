namespace UtmOrchestrator.Core.Tokens;

/// <summary>
/// Один подключённый PKCS#11 токен. Первичный ключ идентификации — <see cref="Serial"/>
/// (аппаратный, есть всегда, в т.ч. у пустого токена). <see cref="FsrarId"/> — вторичный,
/// может отсутствовать (появляется после выпуска ФСРАР-подписи через веб-интерфейс УТМ).
/// </summary>
public sealed record TokenInfo(
    ulong SlotId,
    string ReaderName,
    string Serial,
    string? FsrarId)
{
    public bool HasFsrar => !string.IsNullOrEmpty(FsrarId);
}
