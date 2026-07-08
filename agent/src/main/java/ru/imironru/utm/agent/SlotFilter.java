package ru.imironru.utm.agent;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.io.ByteArrayInputStream;

/**
 * Фильтрует PKCS#11 слоты по FSRAR-ID сертификата.
 *
 * Точка входа из SlotSelectorAdvice: findByFsrarId(cryptographer, defaultSlot).
 * Получает экземпляр sun.security.pkcs11.wrapper.PKCS11 из полей SunCryptographer
 * через reflection, затем перебирает слоты и читает X.509 сертификаты.
 */
public class SlotFilter {

    // OID поля FSRAR-ID в сертификате КЭП ЕГАИС
    private static final String FSRAR_ID_OID = "1.2.643.5.1.10.7.1";

    private static final long CKO_CERTIFICATE = 1L;
    private static final long CKA_CLASS       = 0L;
    private static final long CKA_VALUE       = 17L;
    private static final long SESSION_FLAGS   = 4L; // CKF_SERIAL_SESSION

    private static volatile String targetFsrarId;

    public static void setTarget(String fsrarId) {
        targetFsrarId = fsrarId;
        AgentLogger.info("Целевой FSRAR-ID: " + fsrarId);
    }

    /**
     * Вызывается из SlotSelectorAdvice после SunCryptographer.c(String).
     *
     * @param cryptographer экземпляр SunCryptographer
     * @param defaultSlot   слот, выбранный оригинальным алгоритмом UTM
     * @return слот с нужным FSRAR-ID, или defaultSlot если не найден
     */
    public static long findByFsrarId(Object cryptographer, long defaultSlot) {
        if (targetFsrarId == null || targetFsrarId.isEmpty()) return defaultSlot;

        AgentLogger.info("SunCryptographer.c вызван, defaultSlot=" + defaultSlot
                + ", ищем fsrarId=" + targetFsrarId);

        Object pkcs11 = findPkcs11Instance(cryptographer);
        if (pkcs11 == null) {
            AgentLogger.warn("Поле PKCS11 не найдено в " + cryptographer.getClass().getName());
            return defaultSlot;
        }

        try {
            Method getSlotList = findMethod(pkcs11.getClass(), "C_GetSlotList", boolean.class);
            long[] slots = (long[]) getSlotList.invoke(pkcs11, Boolean.TRUE);
            AgentLogger.info("C_GetSlotList: " + (slots != null ? slots.length : 0) + " слотов");
            if (slots == null || slots.length == 0) return defaultSlot;

            long[] result = scanSlots(pkcs11, slots);
            if (result != null && result.length > 0) return result[0];
        } catch (Throwable t) {
            AgentLogger.error("findByFsrarId: " + t);
        }
        return defaultSlot;
    }

    /** Ищет поле типа *PKCS11* в иерархии классов объекта. */
    private static Object findPkcs11Instance(Object obj) {
        Class<?> cls = obj.getClass();
        while (cls != null && cls != Object.class) {
            for (Field f : cls.getDeclaredFields()) {
                if (f.getType().getName().contains("PKCS11")) {
                    f.setAccessible(true);
                    try { return f.get(obj); } catch (Exception ignored) {}
                }
            }
            cls = cls.getSuperclass();
        }
        return null;
    }

    /**
     * Перебирает слоты: открывает сессию, читает CKO_CERTIFICATE, парсит X.509,
     * ищет OID FSRAR_ID_OID, возвращает массив из одного слота при совпадении.
     */
    private static long[] scanSlots(Object pkcs11, long[] slots) {
        if (slots.length == 1) {
            AgentLogger.info("Один слот — фильтрация пропущена.");
            return null;
        }

        try {
            Class<?> pkcs11Class = pkcs11.getClass();
            Method openSession  = findMethod(pkcs11Class, "C_OpenSession",
                                             long.class, long.class, Object.class, Object.class);
            Method closeSession = findMethod(pkcs11Class, "C_CloseSession", long.class);
            Method findInit     = findMethod(pkcs11Class, "C_FindObjectsInit",
                                             long.class, Object[].class);
            Method findObjects  = findMethod(pkcs11Class, "C_FindObjects", long.class, int.class);
            Method findFinal    = findMethod(pkcs11Class, "C_FindObjectsFinal", long.class);
            Method getAttrVal   = findMethod(pkcs11Class, "C_GetAttributeValue",
                                             long.class, long.class, Object[].class);

            Class<?> ckAttrClass = Class.forName(
                "sun.security.pkcs11.wrapper.CK_ATTRIBUTE",
                false, pkcs11Class.getClassLoader()
            );
            java.lang.reflect.Constructor<?> ctor2 =
                ckAttrClass.getDeclaredConstructor(long.class, Object.class);
            java.lang.reflect.Constructor<?> ctor1 =
                ckAttrClass.getDeclaredConstructor(long.class);
            ctor2.setAccessible(true);
            ctor1.setAccessible(true);
            Field pValueField = ckAttrClass.getDeclaredField("pValue");
            pValueField.setAccessible(true);

            for (long slot : slots) {
                long session = -1L;
                try {
                    session = (Long) openSession.invoke(pkcs11, slot, SESSION_FLAGS, null, null);
                    Object filterAttr = ctor2.newInstance(CKA_CLASS, CKO_CERTIFICATE);
                    findInit.invoke(pkcs11, session, new Object[]{filterAttr});
                    long[] handles = (long[]) findObjects.invoke(pkcs11, session, 64);
                    findFinal.invoke(pkcs11, session);

                    if (handles == null || handles.length == 0) {
                        AgentLogger.info("slot=" + slot + " сертификатов нет");
                        continue;
                    }

                    for (long handle : handles) {
                        Object valAttr = ctor1.newInstance(CKA_VALUE);
                        getAttrVal.invoke(pkcs11, session, handle, new Object[]{valAttr});
                        byte[] der = (byte[]) pValueField.get(valAttr);
                        if (der == null) continue;

                        String fsrarId = extractFsrarId(der);
                        AgentLogger.info("slot=" + slot + " cert fsrarId="
                                + (fsrarId != null ? fsrarId : "<нет>"));

                        if (targetFsrarId.equalsIgnoreCase(fsrarId)) {
                            AgentLogger.info("Совпадение! Выбираем слот " + slot);
                            return new long[]{slot};
                        }
                    }
                } catch (Exception e) {
                    AgentLogger.warn("Ошибка при чтении слота " + slot + ": " + e.getMessage());
                } finally {
                    if (session >= 0) {
                        try { closeSession.invoke(pkcs11, session); } catch (Exception ignored) {}
                    }
                }
            }

            AgentLogger.warn("fsrarId=" + targetFsrarId + " не найден среди "
                    + slots.length + " слотов. Fallback на defaultSlot.");
        } catch (Throwable t) {
            AgentLogger.error("scanSlots: " + t);
        }
        return null;
    }

    /** Извлекает FSRAR-ID (OID 1.2.643.5.1.10.7.1) из DER X.509 сертификата. */
    static String extractFsrarId(byte[] derCert) {
        try {
            CertificateFactory cf = CertificateFactory.getInstance("X.509");
            X509Certificate cert = (X509Certificate) cf.generateCertificate(
                new ByteArrayInputStream(derCert)
            );
            String dn = cert.getSubjectX500Principal().getName("RFC2253");
            return extractOidValue(dn, FSRAR_ID_OID);
        } catch (Exception e) {
            AgentLogger.warn("Не удалось распарсить сертификат: " + e.getMessage());
            return null;
        }
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

    private static Method findMethod(Class<?> cls, String name, Class<?>... params)
            throws NoSuchMethodException {
        try {
            Method m = cls.getDeclaredMethod(name, params);
            m.setAccessible(true);
            return m;
        } catch (NoSuchMethodException e) {
            Method m = cls.getMethod(name, params);
            m.setAccessible(true);
            return m;
        }
    }
}
