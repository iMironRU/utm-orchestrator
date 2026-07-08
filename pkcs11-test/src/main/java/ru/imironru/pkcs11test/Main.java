package ru.imironru.pkcs11test;

import com.sun.jna.Memory;
import com.sun.jna.Native;
import com.sun.jna.NativeLong;
import com.sun.jna.Pointer;
import com.sun.jna.ptr.NativeLongByReference;

import java.io.ByteArrayInputStream;
import java.nio.charset.StandardCharsets;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;

import ru.rutoken.pkcs11jna.CK_ATTRIBUTE;
import ru.rutoken.pkcs11jna.CK_C_INITIALIZE_ARGS;
import ru.rutoken.pkcs11jna.CK_TOKEN_INFO;
import ru.rutoken.pkcs11jna.Pkcs11;
import ru.rutoken.pkcs11jna.Pkcs11Constants;

/**
 * Автономный тест PKCS#11 для Rutoken ECP — работает независимо от UTM/Transport,
 * через прямую загрузку rtPKCS11ECP.dll (JNA), без reflection в JDK-обёртку.
 *
 * Usage:
 *   java -jar pkcs11-test.jar                 — только список токенов (без PIN)
 *   java -jar pkcs11-test.jar <slot>           — чтение сертификата БЕЗ логина (проверка гипотезы)
 *   java -jar pkcs11-test.jar <slot> <pin>     — + логин и чтение сертификата на slot
 */
public class Main {

    private static final String LIB_PATH = "C:\\Windows\\System32\\rtPKCS11ECP.dll";

    public static void main(String[] args) throws Exception {
        Pkcs11 pkcs11 = Native.load(LIB_PATH, Pkcs11.class);

        CK_C_INITIALIZE_ARGS initArgs = new CK_C_INITIALIZE_ARGS();
        initArgs.flags = new NativeLong(Pkcs11Constants.CKF_OS_LOCKING_OK);
        rv("C_Initialize", pkcs11.C_Initialize(initArgs));

        try {
            NativeLongByReference countRef = new NativeLongByReference();
            rv("C_GetSlotList(count)", pkcs11.C_GetSlotList((byte) 1, null, countRef));
            int count = countRef.getValue().intValue();
            System.out.println("Слотов с токенами: " + count);
            if (count == 0) return;

            NativeLong[] slots = new NativeLong[count];
            for (int i = 0; i < count; i++) slots[i] = new NativeLong();
            rv("C_GetSlotList(slots)", pkcs11.C_GetSlotList((byte) 1, slots, countRef));

            for (NativeLong slot : slots) {
                CK_TOKEN_INFO info = new CK_TOKEN_INFO();
                long code = pkcs11.C_GetTokenInfo(slot, info).longValue();
                if (code != Pkcs11Constants.CKR_OK) {
                    System.out.println("slot=" + slot.intValue() + ": C_GetTokenInfo rv=" + code);
                    continue;
                }
                System.out.println("slot=" + slot.intValue()
                        + " serial=" + trim(info.serialNumber)
                        + " label=" + trim(info.label)
                        + " model=" + trim(info.model));
            }

            if (args.length >= 1) {
                int slotNum = Integer.parseInt(args[0]);
                String pin = args.length >= 2 ? args[1] : null;
                NativeLong targetSlot = null;
                for (NativeLong s : slots) {
                    if (s.intValue() == slotNum) { targetSlot = s; break; }
                }
                if (targetSlot == null) {
                    System.out.println("slot=" + slotNum + " не найден среди обнаруженных");
                    return;
                }
                testReadCert(pkcs11, targetSlot, pin);
            } else {
                System.out.println("(чтобы протестировать чтение: java -jar pkcs11-test.jar <slot> [pin])");
            }
        } finally {
            pkcs11.C_Finalize(null);
        }
    }

    /** pin == null — не логинимся вообще, проверяем, что доступно без аутентификации. */
    private static void testReadCert(Pkcs11 pkcs11, NativeLong slot, String pin) {
        NativeLongByReference sessionRef = new NativeLongByReference();
        long code = pkcs11.C_OpenSession(slot, new NativeLong(Pkcs11Constants.CKF_SERIAL_SESSION),
                null, null, sessionRef).longValue();
        if (rv("C_OpenSession", code) != Pkcs11Constants.CKR_OK) return;
        NativeLong session = sessionRef.getValue();
        boolean loggedIn = false;

        try {
            if (pin != null) {
                byte[] pinBytes = pin.getBytes(StandardCharsets.UTF_8);
                code = pkcs11.C_Login(session, new NativeLong(Pkcs11Constants.CKU_USER),
                        pinBytes, new NativeLong(pinBytes.length)).longValue();
                rv("C_Login", code);
                loggedIn = (code == Pkcs11Constants.CKR_OK);
                if (!loggedIn) return;
            } else {
                System.out.println("(без логина — проверяем, что доступно анонимно)");
            }

            CK_ATTRIBUTE[] filter = (CK_ATTRIBUTE[]) new CK_ATTRIBUTE().toArray(1);
            filter[0].setAttr(new NativeLong(Pkcs11Constants.CKA_CLASS),
                    new NativeLong(Pkcs11Constants.CKO_CERTIFICATE));

            code = pkcs11.C_FindObjectsInit(session, filter, new NativeLong(filter.length)).longValue();
            if (rv("C_FindObjectsInit", code) != Pkcs11Constants.CKR_OK) return;

            NativeLong[] handles = new NativeLong[16];
            for (int i = 0; i < handles.length; i++) handles[i] = new NativeLong();
            NativeLongByReference foundRef = new NativeLongByReference();
            code = pkcs11.C_FindObjects(session, handles, new NativeLong(handles.length), foundRef).longValue();
            int found = foundRef.getValue().intValue();
            rv("C_FindObjects (found=" + found + ")", code);
            pkcs11.C_FindObjectsFinal(session);

            for (int i = 0; i < found; i++) {
                NativeLong handle = handles[i];

                byte[] classVal = readAttr(pkcs11, session, handle, Pkcs11Constants.CKA_CLASS, "CKA_CLASS");
                byte[] subjVal  = readAttr(pkcs11, session, handle, Pkcs11Constants.CKA_SUBJECT, "CKA_SUBJECT");
                byte[] der      = readAttr(pkcs11, session, handle, Pkcs11Constants.CKA_VALUE, "CKA_VALUE");

                if (subjVal != null) {
                    try {
                        javax.security.auth.x500.X500Principal p = new javax.security.auth.x500.X500Principal(subjVal);
                        System.out.println("handle=" + handle.intValue() + " CKA_SUBJECT DN=" + p.getName("RFC2253"));
                    } catch (Exception e) {
                        System.out.println("handle=" + handle.intValue() + ": CKA_SUBJECT не распарсился: " + e);
                    }
                }

                if (der != null) {
                    try {
                        CertificateFactory cf = CertificateFactory.getInstance("X.509");
                        X509Certificate cert = (X509Certificate) cf.generateCertificate(new ByteArrayInputStream(der));
                        String dn = cert.getSubjectX500Principal().getName("RFC2253");
                        System.out.println("handle=" + handle.intValue() + " CKA_VALUE subject=" + dn);
                    } catch (Exception e) {
                        System.out.println("handle=" + handle.intValue() + ": не удалось распарсить сертификат: " + e);
                    }
                }
            }
        } finally {
            if (loggedIn) {
                try { pkcs11.C_Logout(session); } catch (Exception ignored) {}
            }
            try { pkcs11.C_CloseSession(session); } catch (Exception ignored) {}
        }
    }

    /** Двухфазный протокол C_GetAttributeValue: сначала длина, потом сами данные. */
    private static byte[] readAttr(Pkcs11 pkcs11, NativeLong session, NativeLong handle, long attrType, String label) {
        CK_ATTRIBUTE[] query = (CK_ATTRIBUTE[]) new CK_ATTRIBUTE().toArray(1);
        query[0].type = new NativeLong(attrType);
        query[0].pValue = Pointer.NULL;
        query[0].ulValueLen = new NativeLong(0);
        query[0].write();

        long code = pkcs11.C_GetAttributeValue(session, handle, query, new NativeLong(query.length)).longValue();
        query[0].read();
        long len = query[0].ulValueLen.longValue();
        System.out.println("handle=" + handle.intValue() + " " + label + "(длина) rv=" + code + " len=" + len);
        if (code != Pkcs11Constants.CKR_OK || len <= 0 || len == 0xFFFFFFFFL) return null;

        Memory buf = new Memory(len);
        CK_ATTRIBUTE[] fetch = (CK_ATTRIBUTE[]) new CK_ATTRIBUTE().toArray(1);
        fetch[0].type = new NativeLong(attrType);
        fetch[0].pValue = buf;
        fetch[0].ulValueLen = new NativeLong(len);
        fetch[0].write();

        code = pkcs11.C_GetAttributeValue(session, handle, fetch, new NativeLong(fetch.length)).longValue();
        System.out.println("handle=" + handle.intValue() + " " + label + "(данные) rv=" + code);
        if (code != Pkcs11Constants.CKR_OK) return null;

        return buf.getByteArray(0, (int) len);
    }

    private static long rv(String op, long code) {
        System.out.println(op + " rv=" + code + (code == Pkcs11Constants.CKR_OK ? " (OK)" : ""));
        return code;
    }

    private static long rv(String op, NativeLong code) {
        return rv(op, code.longValue());
    }

    private static String trim(byte[] b) {
        return b == null ? "" : new String(b, StandardCharsets.UTF_8).trim();
    }
}
