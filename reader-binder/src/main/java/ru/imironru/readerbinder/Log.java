package ru.imironru.readerbinder;

import java.io.IOException;
import java.io.PrintWriter;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.time.LocalDateTime;
import java.time.format.DateTimeFormatter;

/**
 * Простой лог в файл + консоль. В отличие от 2UTM (который в основном
 * молчит при успехе и не всегда явно объясняет ошибку), здесь каждый шаг
 * (forget/introduce/start/wait) логируется явно, с результатом.
 */
public class Log {
    private static final DateTimeFormatter FMT = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss");
    private static PrintWriter file;

    public static synchronized void init() {
        try {
            Path logsDir = Paths.get("logs");
            Files.createDirectories(logsDir);
            String name = "reader-binder_" + LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd_HHmmss")) + ".log";
            file = new PrintWriter(Files.newBufferedWriter(logsDir.resolve(name)), true);
        } catch (IOException e) {
            System.err.println("Не удалось открыть лог-файл: " + e.getMessage());
        }
    }

    public static synchronized void info(String msg) { write("INFO", msg); }
    public static synchronized void warn(String msg) { write("WARN", msg); }
    public static synchronized void error(String msg) { write("ERROR", msg); }

    private static void write(String level, String msg) {
        String line = LocalDateTime.now().format(FMT) + " " + level + "  " + msg;
        System.out.println(line);
        if (file != null) file.println(line);
    }
}
