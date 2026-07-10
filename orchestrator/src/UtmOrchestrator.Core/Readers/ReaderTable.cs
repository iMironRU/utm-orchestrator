using System.Text;
using UtmOrchestrator.Core.Native;

namespace UtmOrchestrator.Core.Readers;

/// <summary>
/// Обёртка над PC/SC-таблицей ридеров (winscard.dll): список ридеров, их
/// SCARD_ATTR_DEVICE_SYSTEM_NAME, и операции forget/introduce.
///
/// ВАЖНО (см. NOTES §6.14): device system name на этих Rutoken самореференциально
/// (= текущее имя ридера), поэтому обратные переименования «туда-обратно»
/// разъезжаются. Первичная идентификация токенов — по серийнику (TokenScanner),
/// а сброс к нативному состоянию — через ForgetAll + перезапуск SCardSvr
/// (см. ReaderReset в связке с ServiceControl), а НЕ через попытки «вернуть как
/// было» переименованиями.
/// </summary>
public sealed class ReaderTable : IDisposable
{
    private readonly IntPtr _ctx;
    private bool _disposed;

    public ReaderTable()
    {
        int rv = WinScard.SCardEstablishContext(WinScard.SCARD_SCOPE_SYSTEM, IntPtr.Zero, IntPtr.Zero, out _ctx);
        if (rv != WinScard.SCARD_S_SUCCESS)
            throw new InvalidOperationException($"SCardEstablishContext failed: 0x{rv:X8}");
    }

    /// <summary>Текущие имена ридеров. Пусто, если ридеров нет.</summary>
    public IReadOnlyList<string> ListReaders()
    {
        int cch = 0;
        int rv = WinScard.SCardListReaders(_ctx, null, null, ref cch);
        if (rv == WinScard.SCARD_E_NO_READERS_AVAILABLE) return Array.Empty<string>();
        if (rv != WinScard.SCARD_S_SUCCESS)
            throw new InvalidOperationException($"SCardListReaders(len) failed: 0x{rv:X8}");
        if (cch <= 0) return Array.Empty<string>();

        var buf = new byte[cch];
        rv = WinScard.SCardListReaders(_ctx, null, buf, ref cch);
        if (rv == WinScard.SCARD_E_NO_READERS_AVAILABLE) return Array.Empty<string>();
        if (rv != WinScard.SCARD_S_SUCCESS)
            throw new InvalidOperationException($"SCardListReaders failed: 0x{rv:X8}");

        return ParseMultiString(buf, cch);
    }

    /// <summary>SCARD_ATTR_DEVICE_SYSTEM_NAME для ридера с данным (текущим) именем.</summary>
    public string GetDeviceSystemName(string readerName)
    {
        int rv = WinScard.SCardConnect(_ctx, readerName, WinScard.SCARD_SHARE_SHARED,
            WinScard.SCARD_PROTOCOL_T0 | WinScard.SCARD_PROTOCOL_T1, out IntPtr hCard, out _);
        if (rv != WinScard.SCARD_S_SUCCESS)
            throw new InvalidOperationException($"SCardConnect('{readerName}') failed: 0x{rv:X8}");
        try
        {
            int len = 256;
            var buf = new byte[len];
            rv = WinScard.SCardGetAttrib(hCard, WinScard.SCARD_ATTR_DEVICE_SYSTEM_NAME, buf, ref len);
            if (rv != WinScard.SCARD_S_SUCCESS)
                throw new InvalidOperationException($"SCardGetAttrib('{readerName}') failed: 0x{rv:X8}");
            return Encoding.ASCII.GetString(buf, 0, len).Replace("\0", string.Empty).Trim();
        }
        finally
        {
            WinScard.SCardDisconnect(hCard, WinScard.SCARD_LEAVE_CARD);
        }
    }

    /// <summary>Забыть логический алиас ридера (SCardForgetReader).</summary>
    public int ForgetReader(string readerName) => WinScard.SCardForgetReader(_ctx, readerName);

    /// <summary>Ввести алиас readerName для устройства deviceSystemName.</summary>
    public int IntroduceReader(string readerName, string deviceSystemName)
        => WinScard.SCardIntroduceReader(_ctx, readerName, deviceSystemName);

    /// <summary>
    /// Забыть ВСЕ текущие алиасы ридеров. После этого список пустеет; физические
    /// токены перерегистрируются с нативными именами только после PnP-триггера —
    /// программно это перезапуск SCardSvr (см. ReaderReset). Возвращает число забытых.
    /// </summary>
    public int ForgetAllReaders()
    {
        int n = 0;
        foreach (var r in ListReaders())
        {
            WinScard.SCardForgetReader(_ctx, r);
            n++;
        }
        return n;
    }

    private static List<string> ParseMultiString(byte[] buf, int length)
    {
        // multi-string: строки ANSI, разделённые \0, оканчиваются двойным \0
        var text = Encoding.ASCII.GetString(buf, 0, length);
        var result = new List<string>();
        foreach (var part in text.Split('\0'))
            if (part.Length > 0) result.Add(part);
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WinScard.SCardReleaseContext(_ctx);
    }
}
