using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using UtmOrchestrator.Core.Firewall;

namespace UtmOrchestrator.Core.Recovery;

/// <summary>
/// Смена внешнего порта УТМ (session-0, службой). Порт УТМ живёт в двух файлах внутри
/// папки УТМ: транспортёр слушает на <c>web.server.port</c>
/// (<c>transporter\conf\transport.properties</c>), а агент обращается к нему по
/// <c>utm.port</c> (<c>agent\conf\agent.properties</c>) — правим оба, иначе агент не
/// найдёт транспортёр. Затем переносим правило брандмауэра и перезапускаем УТМ той же
/// introduce-хореографией, что и обычный рестарт (нужно, т.к. служба на старте читает
/// токен из slot0). Порт в state.json обновляет вызывающий после успеха.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PortChanger
{
    public record Result(bool Success, string Message);

    /// <summary>
    /// Меняет порт oldPort→newPort для службы. Правит конфиги, переносит правило
    /// брандмауэра (сохраняя открыт/закрыт), перезапускает УТМ и проверяет ответ на
    /// новом порту. state.json НЕ трогает — это делает вызывающий при Success.
    /// </summary>
    public static Result Change(
        string folderPath, string service, int oldPort, int newPort,
        string? expectedSerial, string? expectedFsrar, string? readerName,
        IReadOnlyList<string> allReaders, Action<string> log)
    {
        log($"=== смена порта {service}: {oldPort} -> {newPort} ===");

        string transportCfg = Path.Combine(folderPath, "transporter", "conf", "transport.properties");
        string agentCfg = Path.Combine(folderPath, "agent", "conf", "agent.properties");

        if (!File.Exists(transportCfg)) return new(false, $"не найден {transportCfg}");
        if (!File.Exists(agentCfg)) return new(false, $"не найден {agentCfg}");

        // Запоминаем, был ли порт открыт в брандмауэре — чтобы сохранить состояние.
        bool wasOpen = FirewallInspector.IsOpen(oldPort);

        try
        {
            if (!ReplaceKey(transportCfg, "web.server.port", newPort, log))
                return new(false, "не удалось заменить web.server.port");
            if (!ReplaceKey(agentCfg, "utm.port", newPort, log))
            {
                // откат transport.properties, чтобы не оставить рассинхрон
                ReplaceKey(transportCfg, "web.server.port", oldPort, log);
                return new(false, "не удалось заменить utm.port (transport.properties откачен)");
            }
        }
        catch (Exception e)
        {
            return new(false, "ошибка правки конфигов: " + e.Message);
        }

        // Брандмауэр: убрать наше правило на старый порт, при необходимости открыть новый.
        try { FirewallManager.MovePort(oldPort, newPort, wasOpen, log); }
        catch (Exception e) { log("предупреждение: брандмауэр — " + e.Message); }

        // Перезапуск на новом порту (та же introduce-хореография).
        var target = new BootBringUp.Target(service, newPort, expectedSerial ?? "", expectedFsrar, readerName);
        try
        {
            BootBringUp.RestartOne(target, allReaders, log);
        }
        catch (Exception e)
        {
            return new(false, "перезапуск на новом порту не удался: " + e.Message);
        }

        log($"=== порт изменён: {service} теперь на {newPort} ===");
        return new(true, $"порт изменён на {newPort}");
    }

    // Заменяет строго строку "<key>=<...>" (в начале строки) на "<key>=<value>",
    // не трогая остальные ключи (например transport.service.port).
    private static bool ReplaceKey(string path, string key, int value, Action<string> log)
    {
        string text = File.ReadAllText(path);
        var rx = new Regex(@"(?m)^(\s*" + Regex.Escape(key) + @"\s*=).*$");
        if (!rx.IsMatch(text))
        {
            log($"ключ {key} не найден в {Path.GetFileName(path)}");
            return false;
        }
        string updated = rx.Replace(text, "${1}" + value, 1);
        File.WriteAllText(path, updated);
        log($"{Path.GetFileName(path)}: {key}={value}");
        return true;
    }
}
