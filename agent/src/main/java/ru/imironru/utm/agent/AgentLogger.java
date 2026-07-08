package ru.imironru.utm.agent;

import java.io.*;
import java.nio.file.*;
import java.text.SimpleDateFormat;
import java.util.Date;

/**
 * Простой файловый логгер агента.
 * Пишет в %APPDATA%/utm-update/logs/agent_YYYYMMDD_HHmmss.log.
 * Без внешних зависимостей — намеренно, чтобы не конфликтовать с UTM.
 */
public class AgentLogger {

    private static final String PREFIX = "[utm-agent] ";
    private static PrintWriter writer;
    private static final SimpleDateFormat TS = new SimpleDateFormat("yyyy-MM-dd HH:mm:ss");

    static void init(AgentConfig config) {
        try {
            String dir = config.resolveLogDir();
            Files.createDirectories(Paths.get(dir));
            String ts = new SimpleDateFormat("yyyyMMdd_HHmmss").format(new Date());
            File logFile = new File(dir, "agent_" + ts + ".log");
            writer = new PrintWriter(new FileWriter(logFile, true), true);
            info("Лог агента: " + logFile.getAbsolutePath());
        } catch (IOException e) {
            // Fallback: stderr (видно в Windows Event Log если UTM пишет stderr)
            writer = new PrintWriter(System.err, true);
        }
    }

    public static void info(String msg)  { log("INFO ", msg); }
    public static void warn(String msg)  { log("WARN ", msg); }
    public static void error(String msg) { log("ERROR", msg); }

    private static void log(String level, String msg) {
        String line = TS.format(new Date()) + " " + level + " " + PREFIX + msg;
        if (writer != null) {
            writer.println(line);
        } else {
            System.err.println(line);
        }
    }
}
