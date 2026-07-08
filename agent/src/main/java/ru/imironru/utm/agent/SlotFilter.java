package ru.imironru.utm.agent;

import com.sun.jna.Memory;
import com.sun.jna.Native;
import com.sun.jna.NativeLong;
import com.sun.jna.Pointer;
import com.sun.jna.ptr.NativeLongByReference;
import ru.rutoken.pkcs11jna.CK_ATTRIBUTE;
import ru.rutoken.pkcs11jna.CK_C_INITIALIZE_ARGS;
import ru.rutoken.pkcs11jna.Pkcs11;
import ru.rutoken.pkcs11jna.Pkcs11Constants;

import java.io.ByteArrayInputStream;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;

/**
 * Фильтрует PKCS#11 слоты по FSRAR-ID сертификата.
 *
 * Открывает СВОЮ, независимую от UTM сессию к тому же драйверу Rutoken
 * напрямую через JNA (pkcs11jna), а не через reflection в JDK-обёртку
 * sun.security.pkcs11.wrapper.PKCS11.
 *
 * Причина: JDK-обёртка сама решает, когда и как выделять буфер под
 * C_GetAttributeValue (в её Java-классе CK_ATTRIBUTE нет поля длины —
 * логика двухфазного протокола целиком спрятана в нативном коде JDK).
 * Для этого конкретного драйвера Rutoken эта автоматика не срабатывает —
 * pValue молча остаётся null, без исключения, независимо от логина.
 * При ручной реализации двухфазного протокола (сначала запрос длины
 * через pValue=NULL, потом выделение буфера и повторный запрос) поверх
 * pkcs11jna всё читается корректно, причём БЕЗ C_Login — сертификат не
 * секретный, и это подтверждено эмпирически на реальном токене.
 *
 * FSRAR-ID лежит в CN (Common Name) субъекта организационного
 * сертификата — это подтверждается и собственным сообщением UTM
 * ("FSRAR_ID (CN RSA сертификата) не соответствует..."). У личного
 * сертификата сотрудника CN — ФИО, поэтому ищем среди всех найденных
 * сертификатов тот, где CN выглядит как FSRAR-ID (12 цифр).
 */
public class SlotFilter {

    private static final String LIB_PATH = "C:\\Windows\\System32\\rtPKCS11ECP.dll";
    private static final String FSRAR_ID_OID = "1.2.643.5.1.10.7.1";

    private static volatile String targetFsrarId;
    private static volatile Pkcs11 pkcs11;
    private static volatile boolean initAttempted;

    public static void setTarget(String fsrarId) {
        targetFsrarId = fsrarId;
        AgentLogger.info("Целевой FSRAR-ID: " + fsrarId);
    }

    /**
     * Вызывается из SlotSelectorAdvice после SunCryptographer.c(String).
     *
     * @param cryptographer экземпляр SunCryptographer (не используется — оставлен
     *                      для совместимости сигнатуры с SlotSelectorAdvice)
     * @param defaultSlot   слот, выбранный оригинальным алгоритмом UTM
     * @return слот с нужным FSRAR-ID, или defaultSlot если не найден
     */
    public static long findByFsrarId(Object cryptographer, long defaultSlot) {
        if (targetFsrarId == null || targetFsrarId.isEmpty()) return defaultSlot;

        AgentLogger.info("SunCryptographer.c вызван, defaultSlot=" + defaultSlot
                + ", ищем fsrarId=" + targetFsrarId);

        try {
            Pkcs11 p11 = ensureInitialized();
            if (p11 == null) return defaultSlot;

            NativeLongByReference countRef = new NativeLongByReference();
            p11.C_GetSlotList((byte) 1, null, countRef);
            int count = countRef.getValue().intValue();
            AgentLogger.info("C_GetSlotList: " + count + " слотов");
            if (count == 0) return defaultSlot;

            NativeLong[] slots = new NativeLong[count];
            for (int i = 0; i < count; i++) slots[i] = new NativeLong();
            p11.C_GetSlotList((byte) 1, slots, countRef);

            if (slots.length == 1) {
                AgentLogger.info("Один слот — фильтрация пропущена.");
                return defaultSlot;
            }

            boolean discover = "DISCOVER".equalsIgnoreCase(targetFsrarId);

            for (NativeLong slot : slots) {
                Long match = scanSlot(p11, slot, discover);
                if (match != null) {
                    AgentLogger.info("Совпадение! Выбираем слот " + match);
                    return match;
                }
            }

            if (!discover) {
                AgentLogger.warn("fsrarId=" + targetFsrarId + " не найден среди "
                        + slots.length + " слотов. Fallback на defaultSlot.");
            }
        } catch (Throwable t) {
            AgentLogger.error("findByFsrarId: " + t);
        }
        return defaultSlot;
    }

    /** Загружает драйвер и один раз вызывает C_Initialize (никогда не C_Finalize —
     *  это разрушило бы уже открытую сессию UTM в этом же процессе). */
    private static synchronized Pkcs11 ensureInitialized() {
        if (initAttempted) return pkcs11;
        initAttempted = true;
        try {
            Pkcs11 p11 = Native.load(LIB_PATH, Pkcs11.class);
            CK_C_INITIALIZE_ARGS args = new CK_C_INITIALIZE_ARGS();
            args.flags = new NativeLong(Pkcs11Constants.CKF_OS_LOCKING_OK);
            long rv = p11.C_Initialize(args).longValue();
            if (rv != Pkcs11Constants.CKR_OK && rv != Pkcs11Constants.CKR_CRYPTOKI_ALREADY_INITIALIZED) {
                AgentLogger.warn("C_Initialize rv=" + rv);
                return null;
            }
            pkcs11 = p11;
        } catch (Throwable t) {
            AgentLogger.error("Не удалось загрузить " + LIB_PATH + ": " + t);
        }
        return pkcs11;
    }

    private static Long scanSlot(Pkcs11 p11, NativeLong slot, boolean discover) {
        NativeLongByReference sessionRef = new NativeLongByReference();
        long rv = p11.C_OpenSession(slot, new NativeLong(Pkcs11Constants.CKF_SERIAL_SESSION),
                null, null, sessionRef).longValue();
        if (rv != Pkcs11Constants.CKR_OK) {
            AgentLogger.warn("slot=" + slot.intValue() + ": C_OpenSession rv=" + rv);
            return null;
        }
        NativeLong session = sessionRef.getValue();
        try {
            CK_ATTRIBUTE[] filter = (CK_ATTRIBUTE[]) new CK_ATTRIBUTE().toArray(1);
            filter[0].setAttr(new NativeLong(Pkcs11Constants.CKA_CLASS),
                    new NativeLong(Pkcs11Constants.CKO_CERTIFICATE));

            rv = p11.C_FindObjectsInit(session, filter, new NativeLong(filter.length)).longValue();
            if (rv != Pkcs11Constants.CKR_OK) {
                AgentLogger.warn("slot=" + slot.intValue() + ": C_FindObjectsInit rv=" + rv);
                return null;
            }

            NativeLong[] handles = new NativeLong[16];
            for (int i = 0; i < handles.length; i++) handles[i] = new NativeLong();
            NativeLongByReference foundRef = new NativeLongByReference();
            p11.C_FindObjects(session, handles, new NativeLong(handles.length), foundRef);
            int found = foundRef.getValue().intValue();
            p11.C_FindObjectsFinal(session);

            if (found == 0) {
                AgentLogger.info("slot=" + slot.intValue() + " сертификатов нет");
                return null;
            }

            for (int i = 0; i < found; i++) {
                byte[] der = readAttr(p11, session, handles[i], Pkcs11Constants.CKA_VALUE);
                if (der == null) continue;

                String fsrarId = extractFsrarId(der);
                if (discover) {
                    AgentLogger.info("slot=" + slot.intValue() + " cert fsrarId="
                            + (fsrarId != null ? fsrarId : "<нет>"));
                    continue;
                }

                if (fsrarId != null && targetFsrarId.equalsIgnoreCase(fsrarId)) {
                    return (long) slot.intValue();
                }
            }
        } catch (Throwable t) {
            AgentLogger.warn("Ошибка при чтении slot=" + slot.intValue() + ": " + t);
        } finally {
            try { p11.C_CloseSession(session); } catch (Exception ignored) {}
        }
        return null;
    }

    /** Двухфазный протокол C_GetAttributeValue: сначала длина, потом сами данные. */
    private static byte[] readAttr(Pkcs11 p11, NativeLong session, NativeLong handle, long attrType) {
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

    /** Извлекает FSRAR-ID из DER X.509 сертификата: сперва пробует CN (реальный
     *  формат на этих токенах), затем — OID 1.2.643.5.1.10.7.1 как fallback. */
    static String extractFsrarId(byte[] derCert) {
        try {
            CertificateFactory cf = CertificateFactory.getInstance("X.509");
            X509Certificate cert = (X509Certificate) cf.generateCertificate(new ByteArrayInputStream(derCert));
            String dn = cert.getSubjectX500Principal().getName("RFC2253");

            String cn = extractRdnValue(dn, "CN");
            if (cn != null && cn.matches("\\d{10,14}")) return cn;

            return extractOidValue(dn, FSRAR_ID_OID);
        } catch (Exception e) {
            AgentLogger.warn("Не удалось распарсить сертификат: " + e.getMessage());
            return null;
        }
    }

    private static String extractRdnValue(String dn, String rdnName) {
        String key = rdnName + "=";
        int idx = dn.indexOf(key);
        if (idx < 0) return null;
        int start = idx + key.length();
        int end = dn.indexOf(',', start);
        return (end < 0 ? dn.substring(start) : dn.substring(start, end)).trim();
    }

    private static String extractOidValue(String dn, String oid) {
        String key = "OID." + oid + "=";
        int idx = dn.indexOf(key);
        if (idx < 0) { key = oid + "="; idx = dn.indexOf(key); }
        if (idx < 0) return null;
        int start = idx + key.length();
        int end = dn.indexOf(',', start);
        String raw = (end < 0 ? dn.substring(start) : dn.substring(start, end)).trim();
        return raw.startsWith("#") ? decodeHexString(raw.substring(1)) : raw;
    }

    private static String decodeHexString(String hex) {
        try {
            byte[] bytes = hexToBytes(hex);
            if (bytes.length < 2) return hex;
            int len = bytes[1] & 0xFF;
            int off = 2;
            if (len > 127) {
                int lb = len & 0x7F; off = 2 + lb; len = 0;
                for (int i = 0; i < lb; i++) len = (len << 8) | (bytes[2 + i] & 0xFF);
            }
            return new String(bytes, off, Math.min(len, bytes.length - off), "UTF-8");
        } catch (Exception e) { return hex; }
    }

    private static byte[] hexToBytes(String hex) {
        byte[] data = new byte[hex.length() / 2];
        for (int i = 0; i < data.length; i++)
            data[i] = (byte) ((Character.digit(hex.charAt(i * 2), 16) << 4)
                             + Character.digit(hex.charAt(i * 2 + 1), 16));
        return data;
    }
}
