package ru.imironru.readerbinder;

import com.sun.jna.Memory;
import com.sun.jna.Native;
import com.sun.jna.NativeLong;
import com.sun.jna.Pointer;
import com.sun.jna.ptr.NativeLongByReference;
import ru.rutoken.pkcs11jna.CK_ATTRIBUTE;
import ru.rutoken.pkcs11jna.CK_C_INITIALIZE_ARGS;
import ru.rutoken.pkcs11jna.CK_SLOT_INFO;
import ru.rutoken.pkcs11jna.Pkcs11;
import ru.rutoken.pkcs11jna.Pkcs11Constants;

import java.io.ByteArrayInputStream;
import java.nio.charset.StandardCharsets;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.List;

/**
 * Перечисляет PKCS11-слоты драйвера Rutoken и для каждого возвращает
 * (номер слота, имя ридера из CK_SLOT_INFO.slotDescription, FSRAR-ID
 * организационного сертификата, если найден).
 *
 * Двухфазный протокол C_GetAttributeValue и логика извлечения FSRAR-ID
 * из CN сертификата — те же самые, что уже проверены в agent/SlotFilter.java
 * (см. его javadoc за подробностями про особенности этого драйвера).
 */
public class TokenScanner {

    private static final String LIB_PATH = "C:\\Windows\\System32\\rtPKCS11ECP.dll";
    private static final String FSRAR_ID_OID = "1.2.643.5.1.10.7.1";

    public static class TokenInfo {
        public final int slotIndex;
        public final String readerName;
        public final String fsrarId; // null, если не нашли валидный организационный сертификат

        TokenInfo(int slotIndex, String readerName, String fsrarId) {
            this.slotIndex = slotIndex;
            this.readerName = readerName;
            this.fsrarId = fsrarId;
        }
    }

    public List<TokenInfo> scan() throws Exception {
        Pkcs11 p11 = Native.load(LIB_PATH, Pkcs11.class);
        CK_C_INITIALIZE_ARGS args = new CK_C_INITIALIZE_ARGS();
        args.flags = new NativeLong(Pkcs11Constants.CKF_OS_LOCKING_OK);
        long rv = p11.C_Initialize(args).longValue();
        if (rv != Pkcs11Constants.CKR_OK && rv != Pkcs11Constants.CKR_CRYPTOKI_ALREADY_INITIALIZED) {
            throw new IllegalStateException("C_Initialize rv=" + rv);
        }

        List<TokenInfo> result = new ArrayList<>();

        NativeLongByReference countRef = new NativeLongByReference();
        p11.C_GetSlotList((byte) 1, null, countRef);
        int count = countRef.getValue().intValue();
        if (count == 0) return result;

        NativeLong[] slots = new NativeLong[count];
        for (int i = 0; i < count; i++) slots[i] = new NativeLong();
        p11.C_GetSlotList((byte) 1, slots, countRef);

        for (NativeLong slot : slots) {
            String readerName = readSlotDescription(p11, slot);
            String fsrarId = findFsrarIdInSlot(p11, slot);
            result.add(new TokenInfo(slot.intValue(), readerName, fsrarId));
        }

        return result;
    }

    private String readSlotDescription(Pkcs11 p11, NativeLong slot) {
        CK_SLOT_INFO info = new CK_SLOT_INFO();
        long rv = p11.C_GetSlotInfo(slot, info).longValue();
        if (rv != Pkcs11Constants.CKR_OK) return null;
        // slotDescription — 64 байта, дополненные пробелами (не NUL-terminated) по спеке PKCS11
        return new String(info.slotDescription, StandardCharsets.US_ASCII).trim();
    }

    private String findFsrarIdInSlot(Pkcs11 p11, NativeLong slot) {
        NativeLongByReference sessionRef = new NativeLongByReference();
        long rv = p11.C_OpenSession(slot, new NativeLong(Pkcs11Constants.CKF_SERIAL_SESSION),
                null, null, sessionRef).longValue();
        if (rv != Pkcs11Constants.CKR_OK) return null;

        NativeLong session = sessionRef.getValue();
        try {
            CK_ATTRIBUTE[] filter = (CK_ATTRIBUTE[]) new CK_ATTRIBUTE().toArray(1);
            filter[0].setAttr(new NativeLong(Pkcs11Constants.CKA_CLASS),
                    new NativeLong(Pkcs11Constants.CKO_CERTIFICATE));

            rv = p11.C_FindObjectsInit(session, filter, new NativeLong(filter.length)).longValue();
            if (rv != Pkcs11Constants.CKR_OK) return null;

            NativeLong[] handles = new NativeLong[16];
            for (int i = 0; i < handles.length; i++) handles[i] = new NativeLong();
            NativeLongByReference foundRef = new NativeLongByReference();
            p11.C_FindObjects(session, handles, new NativeLong(handles.length), foundRef);
            int found = foundRef.getValue().intValue();
            p11.C_FindObjectsFinal(session);

            for (int i = 0; i < found; i++) {
                byte[] der = readAttr(p11, session, handles[i], Pkcs11Constants.CKA_VALUE);
                if (der == null) continue;
                String fsrarId = extractFsrarId(der);
                if (fsrarId != null) return fsrarId;
            }
        } catch (Exception ignored) {
            // не наш сертификат/слот — пропускаем
        } finally {
            try { p11.C_CloseSession(session); } catch (Exception ignored) {}
        }
        return null;
    }

    private byte[] readAttr(Pkcs11 p11, NativeLong session, NativeLong handle, long attrType) {
        CK_ATTRIBUTE[] query = (CK_ATTRIBUTE[]) new CK_ATTRIBUTE().toArray(1);
        query[0].type = new NativeLong(attrType);
        query[0].pValue = Pointer.NULL;
        query[0].ulValueLen = new NativeLong(0);
        query[0].write();

        long rv = p11.C_GetAttributeValue(session, handle, query, new NativeLong(query.length)).longValue();
        query[0].read();
        long len = query[0].ulValueLen.longValue();
        if (rv != Pkcs11Constants.CKR_OK || len <= 0 || len == 0xFFFFFFFFL) return null;

        Memory buf = new Memory(len);
        CK_ATTRIBUTE[] fetch = (CK_ATTRIBUTE[]) new CK_ATTRIBUTE().toArray(1);
        fetch[0].type = new NativeLong(attrType);
        fetch[0].pValue = buf;
        fetch[0].ulValueLen = new NativeLong(len);
        fetch[0].write();

        rv = p11.C_GetAttributeValue(session, handle, fetch, new NativeLong(fetch.length)).longValue();
        if (rv != Pkcs11Constants.CKR_OK) return null;

        return buf.getByteArray(0, (int) len);
    }

    private String extractFsrarId(byte[] derCert) {
        try {
            CertificateFactory cf = CertificateFactory.getInstance("X.509");
            X509Certificate cert = (X509Certificate) cf.generateCertificate(new ByteArrayInputStream(derCert));
            String dn = cert.getSubjectX500Principal().getName("RFC2253");

            String cn = extractRdnValue(dn, "CN");
            if (cn != null && cn.matches("\\d{10,14}")) return cn;
            return null;
        } catch (Exception e) {
            return null;
        }
    }

    private String extractRdnValue(String dn, String rdnName) {
        String key = rdnName + "=";
        int idx = dn.indexOf(key);
        if (idx < 0) return null;
        int start = idx + key.length();
        int end = dn.indexOf(',', start);
        return (end < 0 ? dn.substring(start) : dn.substring(start, end)).trim();
    }
}
