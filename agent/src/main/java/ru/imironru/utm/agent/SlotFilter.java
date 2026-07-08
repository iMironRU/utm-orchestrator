package ru.imironru.utm.agent;

import java.lang.reflect.Method;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.io.ByteArrayInputStream;

/**
 * Фильтрует список PKCS#11 слотов, оставляя только слот с нужным FSRAR-ID.
 *
 * Алгоритм:
 *  1. Для каждого слота открываем сессию (C_OpenSession без авторизации — достаточно для чтения).
 *  2. Перечисляем объекты CKO_CERTIFICATE в слоте.
 *  3. Для каждого сертификата парсим X.509, ищем OID 1.2.643.5.1.10.7.1 (FSRAR-ID).
 *  4. Если FSRAR-ID совпадает с целевым — возвращаем [slot].
 *  5. Если ни один слот не подошёл — возвращаем null (сигнал «использовать оригинал»).
 *
 * Работает через reflection на sun.security.pkcs11.wrapper.PKCS11 — именно тот
 * класс который инструментируется, поэтому ClassLoader тот же.
 */
public class SlotFilter {

    // OID поля FSRAR-ID в сертификате КЭП ЕГАИС
    private static final String FSRAR_ID_OID = "1.2.643.5.1.10.7.1";

    // CKO_CERTIFICATE = 1
    private static final long CKO_CERTIFICATE = 1L;
    // CKA_CLASS = 0, CKA_VALUE = 17
    private static final long CKA_CLASS = 0L;
    private static final long CKA_VALUE = 17L;

    // CKF_SERIAL_SESSION = 4, CKF_RW_SESSION = 2
    private static final long SESSION_FLAGS = 4L;

    private static volatile String targetFsrarId;

    public static void setTarget(String fsrarId) {
        targetFsrarId = fsrarId;
        AgentLogger.info("Целевой FSRAR-ID: " + fsrarId);
    }

    /**
     * @param pkcs11  экземпляр sun.security.pkcs11.wrapper.PKCS11
     * @param slots   оригинальный список слотов
     * @return отфильтрованный массив (один слот) или null при ошибке/не найдено
     */
    public static long[] filter(Object pkcs11, long[] slots) {
        if (targetFsrarId == null || slots == null || slots.length == 0) return null;
        if (slots.length == 1) {
            // Единственный слот — нет смысла фильтровать, логируем только
            AgentLogger.info("Один слот в системе, фильтрация пропущена.");
            return null;
        }

        try {
            Class<?> pkcs11Class = pkcs11.getClass();

            // Ищем методы через reflection
            Method openSession  = findMethod(pkcs11Class, "C_OpenSession",  long.class, long.class, Object.class, Object.class);
            Method closeSession = findMethod(pkcs11Class, "C_CloseSession", long.class);
            Method findInit     = findMethod(pkcs11Class, "C_FindObjectsInit", long.class, Object[].class);
            Method findObjects  = findMethod(pkcs11Class, "C_FindObjects",  long.class, int.class);
            Method findFinal    = findMethod(pkcs11Class, "C_FindObjectsFinal", long.class);
            Method getAttrVal   = findMethod(pkcs11Class, "C_GetAttributeValue", long.class, long.class, Object[].class);

            // Классы CK_ATTRIBUTE нужны для передачи атрибутов
            Class<?> ckAttrClass = Class.forName(
                "sun.security.pkcs11.wrapper.CK_ATTRIBUTE",
                false, pkcs11Class.getClassLoader()
            );
            java.lang.reflect.Constructor<?> ckAttrCtor2 =
                ckAttrClass.getDeclaredConstructor(long.class, Object.class);
            java.lang.reflect.Constructor<?> ckAttrCtor1 =
                ckAttrClass.getDeclaredConstructor(long.class);
            ckAttrCtor2.setAccessible(true);
            ckAttrCtor1.setAccessible(true);
            java.lang.reflect.Field pValueField = ckAttrClass.getDeclaredField("pValue");
            pValueField.setAccessible(true);

            for (long slot : slots) {
                long session = -1L;
                try {
                    // Открываем read-only сессию (SESSION_FLAGS=4 = CKF_SERIAL_SESSION без CKF_RW)
                    session = (Long) openSession.invoke(pkcs11, slot, SESSION_FLAGS, null, null);

                    // Ищем объекты CKO_CERTIFICATE
                    Object filterAttr = ckAttrCtor2.newInstance(CKA_CLASS, CKO_CERTIFICATE);
                    findInit.invoke(pkcs11, session, new Object[]{filterAttr});
                    long[] certHandles = (long[]) findObjects.invoke(pkcs11, session, 64);
                    findFinal.invoke(pkcs11, session);

                    if (certHandles == null || certHandles.length == 0) continue;

                    for (long handle : certHandles) {
                        Object valueAttr = ckAttrCtor1.newInstance(CKA_VALUE);
                        getAttrVal.invoke(pkcs11, session, handle, new Object[]{valueAttr});
                        byte[] derBytes = (byte[]) pValueField.get(valueAttr);
                        if (derBytes == null) continue;

                        String fsrarId = extractFsrarId(derBytes);
                        AgentLogger.info("slot=" + slot + " cert fsrarId=" + (fsrarId != null ? fsrarId : "<нет>"));

                        if (targetFsrarId.equalsIgnoreCase(fsrarId)) {
                            AgentLogger.info("Совпадение! Оставляем только слот " + slot);
                            return new long[]{slot};
                        }
                    }
                } catch (Exception e) {
                    AgentLogger.warn("Ошибка при проверке слота " + slot + ": " + e.getMessage());
                } finally {
                    if (session >= 0) {
                        try { closeSession.invoke(pkcs11, session); } catch (Exception ignored) {}
                    }
                }
            }

            AgentLogger.warn("Слот с fsrarId=" + targetFsrarId + " не найден среди " + slots.length + " слотов. Fallback на оригинальный список.");
            return null;

        } catch (Throwable t) {
            AgentLogger.error("Критическая ошибка SlotFilter: " + t.getMessage());
            return null;
        }
    }

    /**
     * Извлекает значение атрибута с OID FSRAR_ID_OID из DER-закодированного X.509 сертификата.
     * Возвращает строку вида "030000123456" или null если атрибут отсутствует.
     */
    static String extractFsrarId(byte[] derCert) {
        try {
            CertificateFactory cf = CertificateFactory.getInstance("X.509");
            X509Certificate cert = (X509Certificate) cf.generateCertificate(
                new ByteArrayInputStream(derCert)
            );
            // Subject: OID может лежать в Subject Alternative Name extension или в Subject DN
            // В КЭП ЕГАИС FSRAR-ID обычно в SubjectDN как OID 1.2.643.5.1.10.7.1
            String dn = cert.getSubjectX500Principal().getName("RFC2253");
            return extractOidValue(dn, FSRAR_ID_OID);
        } catch (Exception e) {
            AgentLogger.warn("Не удалось распарсить сертификат: " + e.getMessage());
            return null;
        }
    }

    /**
     * Ищет значение OID в строке RFC2253 SubjectDN.
     * Формат: "OID.1.2.3=value,CN=..."
     */
    private static String extractOidValue(String dn, String oid) {
        // RFC2253: OID.1.2.643.5.1.10.7.1=#<hex> или OID.x=value
        String searchKey = "OID." + oid + "=";
        int idx = dn.indexOf(searchKey);
        if (idx < 0) {
            // Иногда пишется без OID. prefix
            searchKey = oid + "=";
            idx = dn.indexOf(searchKey);
        }
        if (idx < 0) return null;
        int start = idx + searchKey.length();
        int end = dn.indexOf(',', start);
        String raw = (end < 0 ? dn.substring(start) : dn.substring(start, end)).trim();
        // Если значение в hex (#AABBCC...) — декодируем UTF-8 строку
        if (raw.startsWith("#")) {
            return decodeHexString(raw.substring(1));
        }
        return raw;
    }

    /**
     * Декодирует DER-закодированную строку из hex в читаемый вид.
     * Формат ASN.1: tag(1) + len(1+) + value bytes.
     */
    private static String decodeHexString(String hex) {
        try {
            byte[] bytes = hexToBytes(hex);
            // Пропускаем ASN.1 tag и length, берём value
            if (bytes.length < 2) return hex;
            int len = bytes[1] & 0xFF;
            int valueOffset = 2;
            if (len > 127) {
                int lenBytes = len & 0x7F;
                valueOffset = 2 + lenBytes;
                len = 0;
                for (int i = 0; i < lenBytes; i++) {
                    len = (len << 8) | (bytes[2 + i] & 0xFF);
                }
            }
            return new String(bytes, valueOffset, Math.min(len, bytes.length - valueOffset), "UTF-8");
        } catch (Exception e) {
            return hex;
        }
    }

    private static byte[] hexToBytes(String hex) {
        int len = hex.length();
        byte[] data = new byte[len / 2];
        for (int i = 0; i < len; i += 2) {
            data[i / 2] = (byte) ((Character.digit(hex.charAt(i), 16) << 4)
                                 + Character.digit(hex.charAt(i + 1), 16));
        }
        return data;
    }

    private static Method findMethod(Class<?> cls, String name, Class<?>... params) throws NoSuchMethodException {
        try {
            Method m = cls.getDeclaredMethod(name, params);
            m.setAccessible(true);
            return m;
        } catch (NoSuchMethodException e) {
            // Попробуем в суперклассе
            Method m = cls.getMethod(name, params);
            m.setAccessible(true);
            return m;
        }
    }
}
