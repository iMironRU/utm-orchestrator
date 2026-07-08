package ru.imironru.utm.agent;

/**
 * Разбор аргументов агента.
 * Формат: fsrarId=030000123456[,logDir=C:\path]
 */
public class AgentConfig {

    private String fsrarId = "";
    private String logDir  = null; // null → %APPDATA%\utm-update\logs

    private AgentConfig() {}

    public static AgentConfig parse(String args) {
        AgentConfig cfg = new AgentConfig();
        if (args == null || args.trim().isEmpty()) return cfg;

        for (String part : args.split(",")) {
            int eq = part.indexOf('=');
            if (eq < 0) continue;
            String key = part.substring(0, eq).trim();
            String val = part.substring(eq + 1).trim();
            switch (key) {
                case "fsrarId": cfg.fsrarId = val; break;
                case "logDir":  cfg.logDir  = val; break;
            }
        }
        return cfg;
    }

    public String getFsrarId() { return fsrarId; }

    public String resolveLogDir() {
        if (logDir != null && !logDir.isEmpty()) return logDir;
        String appdata = System.getenv("APPDATA");
        if (appdata == null) appdata = System.getProperty("user.home");
        return appdata + "\\utm-update\\logs";
    }
}
