package ru.imironru.readerbinder;

import com.sun.jna.Native;
import com.sun.jna.Pointer;
import com.sun.jna.ptr.IntByReference;
import com.sun.jna.ptr.PointerByReference;
import com.sun.jna.win32.StdCallLibrary;

/**
 * JNA-биндинги к winscard.dll — тот же набор вызовов, которым 2UTM
 * реализует переименование ридеров (см. ControlReaders.cpp):
 * SCardForgetReader + SCardIntroduceReader меняют алиас логического
 * имени ридера, SCardGetAttrib(SCARD_ATTR_DEVICE_SYSTEM_NAME) читает
 * текущее "системное имя" устройства, стоящего за этим алиасом.
 *
 * SCARDCONTEXT/SCARDHANDLE в WinAPI — это ULONG_PTR (размер указателя,
 * 8 байт на x64), а не "long" (4 байта всегда на Windows) — поэтому
 * везде используется Pointer/PointerByReference, а не NativeLong,
 * как и в уже проверенных PowerShell-прототипах (Get-ReaderAttrs.ps1,
 * Swap-Readers.ps1), где эти поля объявлены как IntPtr.
 */
public interface WinScard extends StdCallLibrary {
    WinScard INSTANCE = Native.load("winscard", WinScard.class);

    int SCARD_S_SUCCESS = 0;
    int SCARD_SCOPE_SYSTEM = 2;
    int SCARD_SHARE_SHARED = 2;
    int SCARD_PROTOCOL_T0 = 1;
    int SCARD_PROTOCOL_T1 = 2;
    int SCARD_LEAVE_CARD = 0;
    // (SCARD_CLASS_SYSTEM=0x7FFF << 16) | 0x0003
    int SCARD_ATTR_DEVICE_SYSTEM_NAME = 0x7FFF0003;

    int SCardEstablishContext(int dwScope, Pointer pvReserved1, Pointer pvReserved2, PointerByReference phContext);

    int SCardReleaseContext(Pointer hContext);

    int SCardListReadersA(Pointer hContext, byte[] mszGroups, byte[] mszReaders, IntByReference pcchReaders);

    int SCardConnectA(Pointer hContext, String szReader, int dwShareMode, int dwPreferredProtocols,
                       PointerByReference phCard, IntByReference pdwActiveProtocol);

    int SCardDisconnect(Pointer hCard, int dwDisposition);

    int SCardGetAttrib(Pointer hCard, int dwAttrId, byte[] pbAttr, IntByReference pcbAttrLen);

    int SCardForgetReaderA(Pointer hContext, String szReaderName);

    int SCardIntroduceReaderA(Pointer hContext, String szReaderName, String szDeviceName);
}
