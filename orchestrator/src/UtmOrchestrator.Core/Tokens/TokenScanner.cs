using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;

namespace UtmOrchestrator.Core.Tokens;

/// <summary>
/// Перечисляет подключённые Rutoken ECP токены через PKCS#11: серийник (C_GetTokenInfo),
/// имя ридера (slotDescription) и ФСРАР-ИД из CN организационного сертификата (если есть).
///
/// Двухфазный протокол C_GetAttributeValue и работа со структурами CK_* спрятаны внутри
/// Pkcs11Interop — в отличие от JDK-обёртки (см. NOTES §5), у которой этот протокол ломался
/// на данном драйвере. Логин (C_Login) не требуется — сертификаты читаются анонимно.
/// </summary>
public sealed class TokenScanner
{
    public const string DefaultLibPath = @"C:\Windows\System32\rtPKCS11ECP.dll";

    // ФСРАР-ИД лежит в CN и выглядит как 12 цифр (03XXXXXXXXXX). У личного сертификата
    // сотрудника CN — ФИО, поэтому фильтруем по «только цифры».
    private static readonly Regex FsrarPattern = new(@"^\d{10,14}$", RegexOptions.Compiled);

    private readonly string _libPath;
    private readonly Pkcs11InteropFactories _factories = new();

    public TokenScanner(string? libPath = null) => _libPath = libPath ?? DefaultLibPath;

    public IReadOnlyList<TokenInfo> Scan()
    {
        var result = new List<TokenInfo>();

        using var pkcs11 = _factories.Pkcs11LibraryFactory.LoadPkcs11Library(
            _factories, _libPath, AppType.SingleThreaded);

        foreach (var slot in pkcs11.GetSlotList(SlotsType.WithTokenPresent))
        {
            string reader, serial;
            try
            {
                reader = slot.GetSlotInfo().SlotDescription?.Trim() ?? string.Empty;
                serial = slot.GetTokenInfo().SerialNumber?.Trim() ?? string.Empty;
            }
            catch
            {
                continue; // токен «моргнул» между перечислением и чтением — пропускаем
            }

            string? fsrar = TryReadFsrar(slot);
            result.Add(new TokenInfo(slot.SlotId, reader, serial, fsrar));
        }

        return result;
    }

    private string? TryReadFsrar(ISlot slot)
    {
        try
        {
            using var session = slot.OpenSession(SessionType.ReadOnly);
            var template = new List<IObjectAttribute>
            {
                _factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
            };

            foreach (var obj in session.FindAllObjects(template))
            {
                var attrs = session.GetAttributeValue(obj, new List<CKA> { CKA.CKA_VALUE });
                byte[] der = attrs[0].GetValueAsByteArray();
                string? fsrar = ExtractFsrar(der);
                if (fsrar != null) return fsrar;
            }
        }
        catch
        {
            // не наш токен/сертификат — не критично
        }
        return null;
    }

    private static string? ExtractFsrar(byte[] der)
    {
        try
        {
            using var cert = new X509Certificate2(der);
            string cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return !string.IsNullOrEmpty(cn) && FsrarPattern.IsMatch(cn) ? cn : null;
        }
        catch
        {
            return null;
        }
    }
}
