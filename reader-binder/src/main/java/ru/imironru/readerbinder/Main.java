package ru.imironru.readerbinder;

import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * reader-binder — переносит рабочий принцип 2UTM (переименование PC/SC
 * ридеров через winscard.dll, чтобы фиксировать, какой физический токен
 * становится "слотом 0" для каждого процесса Transport_N) на нашу же,
 * уже проверенную схему идентификации токена по FSRAR-ID сертификата
 * (вместо серийного номера токена, который 2UTM берёт из config.ini).
 *
 * Как это работает (см. подробный разбор installUTM() в NOTES.md):
 * каждый Transport-процесс один раз, при первом обращении к PKCS11,
 * слепо берёт "слот 0". Поэтому единственный способ гарантировать,
 * что слот 0 в этот момент — "его" токен — это ввести (SCardIntroduceReader)
 * ТОЛЬКО его ридер под самым низким ещё не занятым именем, запустить
 * именно эту службу, ДОЖДАТЬСЯ её первого захвата слота (HTTP отвечает),
 * и только потом вводить следующий (ещё более низкий) ридер и её службу.
 *
 * Режимы:
 *   --verify    только читает текущее состояние (PKCS11-слоты + PC/SC-ридеры
 *               + сверка с bindings.properties), ничего не меняет.
 *   --dry-run   как verify, плюс печатает точный план forget/introduce/
 *               restart/wait, но не выполняет ни одного действия.
 *   --apply     реально выполняет план (требует прав администратора на
 *               sc.exe start/stop и подключённые токены для соответствующих
 *               записей в bindings.properties).
 */
public class Main {

    public static void main(String[] args) throws Exception {
        Log.init();

        String mode = args.length > 0 ? args[0] : "--verify";
        Path configPath = args.length > 1 ? Paths.get(args[1]) : Paths.get("bindings.properties");

        List<BindingConfig.Entry> bindings = BindingConfig.load(configPath);
        Log.info("Загружено записей в " + configPath + ": " + bindings.size());

        TokenScanner.TokenInfo[] tokens = new TokenScanner().scan().toArray(new TokenScanner.TokenInfo[0]);
        Log.info("PKCS11: найдено токенов/слотов: " + tokens.length);
        for (TokenScanner.TokenInfo t : tokens) {
            Log.info(String.format("  слот %d, ридер '%s', fsrarId=%s",
                    t.slotIndex, t.readerName, t.fsrarId != null ? t.fsrarId : "<нет валидного сертификата>"));
        }

        try (ReaderTable readers = new ReaderTable()) {
            List<String> currentReaders = readers.listReaders();
            Log.info("PC/SC: текущие ридеры (" + currentReaders.size() + "): " + currentReaders);

            // fsrarId -> текущее системное имя устройства (до любых переименований)
            Map<String, String> fsrarToDevice = new HashMap<>();
            for (TokenScanner.TokenInfo t : tokens) {
                if (t.fsrarId == null || t.readerName == null) continue;
                try {
                    String deviceName = readers.getDeviceSystemName(t.readerName);
                    fsrarToDevice.put(t.fsrarId, deviceName);
                } catch (Exception e) {
                    Log.warn("Не удалось прочитать SCARD_ATTR_DEVICE_SYSTEM_NAME для '" + t.readerName + "': " + e.getMessage());
                }
            }

            Log.info("--- Сверка с bindings.properties ---");
            for (BindingConfig.Entry e : bindings) {
                String device = fsrarToDevice.get(e.fsrarId);
                if (device == null) {
                    Log.warn(String.format("fsrarId=%s (-> %s, порт %d, желаемый '%s'): токен сейчас НЕ подключён",
                            e.fsrarId, e.serviceName, e.port, e.readerName()));
                    continue;
                }
                boolean alreadyBound = device.equals(e.readerName());
                Log.info(String.format("fsrarId=%s (-> %s, порт %d): сейчас на ридере '%s' (устройство '%s'); нужно '%s' -> %s",
                        e.fsrarId, e.serviceName, e.port, findCurrentReaderName(tokens, e.fsrarId), device, e.readerName(),
                        alreadyBound ? "УЖЕ СВЯЗАНО" : "требуется переименование"));
            }

            if ("--verify".equals(mode)) {
                return;
            }

            Log.info("--- План действий (" + mode + ") ---");
            Log.info("1) Забыть все текущие ридеры: " + currentReaders);
            for (BindingConfig.Entry e : bindings) {
                String device = fsrarToDevice.get(e.fsrarId);
                if (device == null) {
                    Log.warn("Пропуск fsrarId=" + e.fsrarId + " — токен не подключён, план для него не строим.");
                    continue;
                }
                Log.info(String.format("2) Ввести ридер '%s' -> устройство '%s'", e.readerName(), device));
                Log.info(String.format("3) Перезапустить службу '%s' (stop, start)", e.serviceName));
                Log.info(String.format("4) Дождаться готовности http://localhost:%d/diagnosis", e.port));
            }

            if ("--dry-run".equals(mode)) {
                Log.info("--dry-run: ничего не выполнено.");
                return;
            }

            if (!"--apply".equals(mode)) {
                Log.error("Неизвестный режим: " + mode + " (ожидается --verify | --dry-run | --apply)");
                return;
            }

            Log.info("=== ВЫПОЛНЕНИЕ ===");
            for (String r : currentReaders) {
                int rv = readers.forgetReader(r);
                Log.info("SCardForgetReader('" + r + "') rv=0x" + Integer.toHexString(rv));
            }

            for (BindingConfig.Entry e : bindings) {
                String device = fsrarToDevice.get(e.fsrarId);
                if (device == null) continue;

                int rv = readers.introduceReader(e.readerName(), device);
                Log.info("SCardIntroduceReader('" + e.readerName() + "' -> '" + device + "') rv=0x" + Integer.toHexString(rv));
                if (rv != WinScard.SCARD_S_SUCCESS) {
                    Log.error("Не удалось ввести ридер для fsrarId=" + e.fsrarId + " — служба " + e.serviceName + " НЕ перезапущена.");
                    continue;
                }

                ServiceControl.stop(e.serviceName);
                Thread.sleep(500);
                int startRv = ServiceControl.start(e.serviceName);
                Log.info("Запуск службы " + e.serviceName + ", sc.exe rv=" + startRv);

                boolean up = ServiceControl.waitForHttp(e.port, 90);
                if (up) {
                    Log.info("Служба " + e.serviceName + " (порт " + e.port + ") готова — fsrarId=" + e.fsrarId + " должен быть слотом 0.");
                } else {
                    Log.error("Служба " + e.serviceName + " (порт " + e.port + ") НЕ ответила за отведённое время!");
                }
            }
            Log.info("=== ГОТОВО ===");
        }
    }

    private static String findCurrentReaderName(TokenScanner.TokenInfo[] tokens, String fsrarId) {
        for (TokenScanner.TokenInfo t : tokens) {
            if (fsrarId.equals(t.fsrarId)) return t.readerName;
        }
        return "?";
    }
}
