using System.Diagnostics;
using System.Runtime.Versioning;
using UtmOrchestrator.Core.Services;

namespace UtmOrchestrator.Core.Migration;

/// <summary>
/// Управление службой 2UTM при миграции — ОБРАТИМО, без удаления файлов.
/// «Заглушить» = остановить службу + перевести в Disabled + autostart=false в config
/// (чтобы 2UTM не поднимался и не дрался с нами за PC/SC-ридеры на загрузке).
/// «Вернуть» = обратно Automatic + autostart=true + запустить. Уже запущенные УТМ при
/// остановке 2UTM не падают (они держат свой токен; 2UTM важен только на загрузке).
/// Требует прав администратора — служба оркестратора (LocalSystem) их имеет.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TwoUtmControl
{
    /// <summary>Заглушить 2UTM: стоп + Disabled + autostart=false.</summary>
    public static void Disable(string serviceName, string? configPath, Action<string> log)
    {
        if (ServiceControl.GetState(serviceName) == ServiceState.Running)
        {
            log($"Останавливаю службу 2UTM ({serviceName})…");
            try { ServiceControl.Stop(serviceName, TimeSpan.FromSeconds(60)); }
            catch (Exception e) { log($"стоп 2UTM: {e.Message}"); }
        }
        ScConfig(serviceName, "disabled", log);
        if (configPath is not null) TwoUtmConfig.SetAutostart(configPath, false, log);
        log("2UTM заглушён (обратимо: файлы не тронуты).");
    }

    /// <summary>Вернуть 2UTM: Automatic + autostart=true + запуск.</summary>
    public static void Restore(string serviceName, string? configPath, Action<string> log)
    {
        ScConfig(serviceName, "auto", log);
        if (configPath is not null) TwoUtmConfig.SetAutostart(configPath, true, log);
        try { ServiceControl.Start(serviceName, TimeSpan.FromSeconds(60)); log("2UTM возвращён и запущен."); }
        catch (Exception e) { log($"старт 2UTM: {e.Message}"); }
    }

    private static void ScConfig(string name, string start, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo("sc", $"config \"{name}\" start= {start}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            log($"sc config {name} start= {start} -> exit {p.ExitCode}");
        }
        catch (Exception e) { log($"sc config {name}: {e.Message}"); }
    }
}
