package ru.imironru.readerbinder;

import com.sun.jna.Pointer;
import com.sun.jna.ptr.IntByReference;
import com.sun.jna.ptr.PointerByReference;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/**
 * Обёртка над winscard.dll: список текущих PC/SC ридеров, чтение их
 * SCARD_ATTR_DEVICE_SYSTEM_NAME, и операции переименования
 * (SCardForgetReader/SCardIntroduceReader) — те же примитивы, которыми
 * пользуется 2UTM (см. ControlReaders.cpp).
 */
public class ReaderTable implements AutoCloseable {

    private final Pointer context;

    public ReaderTable() {
        PointerByReference ctxRef = new PointerByReference();
        int rv = WinScard.INSTANCE.SCardEstablishContext(WinScard.SCARD_SCOPE_SYSTEM, null, null, ctxRef);
        if (rv != WinScard.SCARD_S_SUCCESS) {
            throw new IllegalStateException("SCardEstablishContext failed: 0x" + Integer.toHexString(rv));
        }
        this.context = ctxRef.getValue();
    }

    public List<String> listReaders() {
        List<String> readers = new ArrayList<>();
        IntByReference cch = new IntByReference(4096);
        byte[] buf = new byte[4096];
        int rv = WinScard.INSTANCE.SCardListReadersA(context, null, buf, cch);
        if (rv == 0x8010002E) { // SCARD_E_NO_READERS_AVAILABLE
            return readers;
        }
        if (rv != WinScard.SCARD_S_SUCCESS) {
            throw new IllegalStateException("SCardListReadersA failed: 0x" + Integer.toHexString(rv));
        }
        String text = new String(buf, 0, cch.getValue(), StandardCharsets.US_ASCII);
        for (String r : text.split("\0")) {
            if (!r.isEmpty()) readers.add(r);
        }
        return readers;
    }

    /** SCARD_ATTR_DEVICE_SYSTEM_NAME для ридера с данным (текущим) именем. */
    public String getDeviceSystemName(String readerName) {
        PointerByReference hCardRef = new PointerByReference();
        IntByReference proto = new IntByReference();
        int rv = WinScard.INSTANCE.SCardConnectA(context, readerName, WinScard.SCARD_SHARE_SHARED,
                WinScard.SCARD_PROTOCOL_T0 | WinScard.SCARD_PROTOCOL_T1, hCardRef, proto);
        if (rv != WinScard.SCARD_S_SUCCESS) {
            throw new IllegalStateException("SCardConnectA(" + readerName + ") failed: 0x" + Integer.toHexString(rv));
        }
        Pointer hCard = hCardRef.getValue();
        try {
            byte[] attrBuf = new byte[256];
            IntByReference attrLen = new IntByReference(attrBuf.length);
            rv = WinScard.INSTANCE.SCardGetAttrib(hCard, WinScard.SCARD_ATTR_DEVICE_SYSTEM_NAME, attrBuf, attrLen);
            if (rv != WinScard.SCARD_S_SUCCESS) {
                throw new IllegalStateException("SCardGetAttrib(" + readerName + ") failed: 0x" + Integer.toHexString(rv));
            }
            return new String(attrBuf, 0, attrLen.getValue(), StandardCharsets.US_ASCII).replace("\0", "");
        } finally {
            WinScard.INSTANCE.SCardDisconnect(hCard, WinScard.SCARD_LEAVE_CARD);
        }
    }

    /** Забыть текущее имя ридера (SCardForgetReader). Не бросает, если ридер уже не найден. */
    public int forgetReader(String readerName) {
        return WinScard.INSTANCE.SCardForgetReaderA(context, readerName);
    }

    /** Ввести новый логический алиас readerName для устройства deviceSystemName. */
    public int introduceReader(String readerName, String deviceSystemName) {
        return WinScard.INSTANCE.SCardIntroduceReaderA(context, readerName, deviceSystemName);
    }

    @Override
    public void close() {
        WinScard.INSTANCE.SCardReleaseContext(context);
    }
}
