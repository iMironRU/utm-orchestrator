using System.Runtime.InteropServices;

namespace UtmOrchestrator.Core.Native;

/// <summary>
/// P/Invoke к winscard.dll — те же вызовы, которыми управляется таблица PC/SC
/// ридеров (см. reader-binder/ReaderTable.java и исходники 2UTM). SCARDCONTEXT/
/// SCARDHANDLE в WinAPI — ULONG_PTR (размер указателя), поэтому IntPtr, а не int.
/// </summary>
internal static class WinScard
{
    public const int SCARD_S_SUCCESS = 0;
    public const int SCARD_SCOPE_SYSTEM = 2;
    public const int SCARD_SHARE_SHARED = 2;
    public const int SCARD_PROTOCOL_T0 = 1;
    public const int SCARD_PROTOCOL_T1 = 2;
    public const int SCARD_LEAVE_CARD = 0;

    // (SCARD_CLASS_SYSTEM=0x7FFF << 16) | 0x0003
    public const uint SCARD_ATTR_DEVICE_SYSTEM_NAME = 0x7FFF0003;
    public const int SCARD_E_NO_READERS_AVAILABLE = unchecked((int)0x8010002E);

    [DllImport("winscard.dll")]
    public static extern int SCardEstablishContext(int dwScope, IntPtr pvReserved1, IntPtr pvReserved2, out IntPtr phContext);

    [DllImport("winscard.dll")]
    public static extern int SCardReleaseContext(IntPtr hContext);

    [DllImport("winscard.dll", CharSet = CharSet.Ansi, EntryPoint = "SCardListReadersA")]
    public static extern int SCardListReaders(IntPtr hContext, byte[]? mszGroups, byte[]? mszReaders, ref int pcchReaders);

    [DllImport("winscard.dll", CharSet = CharSet.Ansi, EntryPoint = "SCardConnectA")]
    public static extern int SCardConnect(IntPtr hContext, string szReader, int dwShareMode, int dwPreferredProtocols,
        out IntPtr phCard, out int pdwActiveProtocol);

    [DllImport("winscard.dll")]
    public static extern int SCardDisconnect(IntPtr hCard, int dwDisposition);

    [DllImport("winscard.dll", EntryPoint = "SCardGetAttrib")]
    public static extern int SCardGetAttrib(IntPtr hCard, uint dwAttrId, byte[]? pbAttr, ref int pcbAttrLen);

    [DllImport("winscard.dll", CharSet = CharSet.Ansi, EntryPoint = "SCardForgetReaderA")]
    public static extern int SCardForgetReader(IntPtr hContext, string szReaderName);

    [DllImport("winscard.dll", CharSet = CharSet.Ansi, EntryPoint = "SCardIntroduceReaderA")]
    public static extern int SCardIntroduceReader(IntPtr hContext, string szReaderName, string szDeviceName);
}
