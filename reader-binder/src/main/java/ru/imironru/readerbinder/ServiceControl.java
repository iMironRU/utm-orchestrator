package ru.imironru.readerbinder;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/**
 * Управление Windows-службами Transport через sc.exe (проще и надёжнее
 * из Java, чем P/Invoke на Advapi32 — как это делает 2UTM в ControlService.cpp)
 * и опрос HTTP-порта службы до готовности (аналог checkHomePageUTM()).
 *
 * Требует прав администратора на реальный sc.exe start/stop — при их
 * отсутствии команда просто вернёт ненулевой код, что мы логируем.
 */
public class ServiceControl {

    public static int run(String... args) throws Exception {
        ProcessBuilder pb = new ProcessBuilder(args);
        pb.redirectErrorStream(true);
        Process p = pb.start();
        StringBuilder out = new StringBuilder();
        try (BufferedReader r = new BufferedReader(new InputStreamReader(p.getInputStream(), StandardCharsets.UTF_8))) {
            String line;
            while ((line = r.readLine()) != null) out.append(line).append('\n');
        }
        int exit = p.waitFor();
        if (exit != 0) {
            Log.warn("Команда " + String.join(" ", args) + " вернула код " + exit + ":\n" + out);
        }
        return exit;
    }

    private static String queryState(String serviceName) throws Exception {
        ProcessBuilder pb = new ProcessBuilder("sc.exe", "query", serviceName);
        pb.redirectErrorStream(true);
        Process p = pb.start();
        StringBuilder out = new StringBuilder();
        try (BufferedReader r = new BufferedReader(new InputStreamReader(p.getInputStream(), StandardCharsets.UTF_8))) {
            String line;
            while ((line = r.readLine()) != null) out.append(line).append('\n');
        }
        p.waitFor();
        String text = out.toString();
        if (text.contains("RUNNING")) return "RUNNING";
        if (text.contains("STOPPED")) return "STOPPED";
        if (text.contains("STOP_PENDING")) return "STOP_PENDING";
        if (text.contains("START_PENDING")) return "START_PENDING";
        return "UNKNOWN";
    }

    public static boolean isRunning(String serviceName) throws Exception {
        return "RUNNING".equals(queryState(serviceName));
    }

    /** Дожидается фактического (не только принятого) состояния, а не фиксированной паузы —
     *  именно недостаточное ожидание после stop привело к "уже запущена" (1056) при start. */
    private static boolean waitForState(String serviceName, String wanted, int timeoutSeconds) throws Exception {
        long deadline = System.currentTimeMillis() + timeoutSeconds * 1000L;
        while (System.currentTimeMillis() < deadline) {
            String state = queryState(serviceName);
            if (wanted.equals(state)) return true;
            Thread.sleep(1000);
        }
        return wanted.equals(queryState(serviceName));
    }

    public static int start(String serviceName) throws Exception {
        int rv = run("sc.exe", "start", serviceName);
        boolean up = waitForState(serviceName, "RUNNING", 60);
        Log.info("Служба " + serviceName + " после start: " + (up ? "RUNNING" : "НЕ RUNNING (таймаут)"));
        return rv;
    }

    public static int stop(String serviceName) throws Exception {
        if (!isRunning(serviceName)) {
            Log.info("Служба " + serviceName + " уже не запущена — stop пропущен.");
            return 0;
        }
        int rv = run("sc.exe", "stop", serviceName);
        boolean stopped = waitForState(serviceName, "STOPPED", 60);
        Log.info("Служба " + serviceName + " после stop: " + (stopped ? "STOPPED" : "НЕ STOPPED (таймаут)"));
        return rv;
    }

    /** Опрашивает http://localhost:port до первого успешного ответа (аналог checkHomePageUTM). */
    public static boolean waitForHttp(int port, int timeoutSeconds) {
        long deadline = System.currentTimeMillis() + timeoutSeconds * 1000L;
        while (System.currentTimeMillis() < deadline) {
            try {
                URL url = new URL("http://localhost:" + port + "/diagnosis");
                HttpURLConnection conn = (HttpURLConnection) url.openConnection();
                conn.setConnectTimeout(2000);
                conn.setReadTimeout(2000);
                conn.setRequestMethod("GET");
                int code = conn.getResponseCode();
                conn.disconnect();
                if (code > 0) return true;
            } catch (Exception ignored) {
                // ещё не поднялся — ждём
            }
            try { Thread.sleep(1000); } catch (InterruptedException ignored) {}
        }
        return false;
    }
}
