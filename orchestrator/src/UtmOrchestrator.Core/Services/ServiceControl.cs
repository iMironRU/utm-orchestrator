using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace UtmOrchestrator.Core.Services;

public enum ServiceState
{
    NotInstalled,
    Stopped,
    StartPending,
    StopPending,
    Running,
    Other,
}

/// <summary>Тип запуска службы (для управления автозагрузкой УТМ).</summary>
public enum StartMode { Automatic, Manual, Disabled, Unknown }

/// <summary>
/// Управление Windows-службами Transport* (start/stop/query с ожиданием).
/// Регистрация/удаление служб (procrun через utm.exe) — отдельно, на этапе
/// установки. Требует прав администратора для start/stop.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceControl
{
    public static ServiceState GetState(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending => ServiceState.StartPending,
                ServiceControllerStatus.StopPending => ServiceState.StopPending,
                ServiceControllerStatus.Running => ServiceState.Running,
                _ => ServiceState.Other,
            };
        }
        catch (InvalidOperationException)
        {
            // служба не установлена
            return ServiceState.NotInstalled;
        }
    }

    public static bool IsRunning(string serviceName) => GetState(serviceName) == ServiceState.Running;

    /// <summary>Текущий тип запуска службы (auto/manual/disabled). Unknown, если не удалось прочитать.</summary>
    public static StartMode GetStartMode(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.StartType switch
            {
                ServiceStartMode.Automatic => StartMode.Automatic,
                ServiceStartMode.Manual => StartMode.Manual,
                ServiceStartMode.Disabled => StartMode.Disabled,
                _ => StartMode.Unknown,
            };
        }
        catch { return StartMode.Unknown; }
    }

    /// <summary>Сменить тип запуска службы через <c>sc config</c> (нужны права администратора;
    /// служба = LocalSystem = админ). true при успехе. Идемпотентно на стороне вызывающего.</summary>
    public static bool SetStartMode(string serviceName, StartMode mode, Action<string>? log = null)
    {
        string arg = mode switch
        {
            StartMode.Automatic => "auto",
            StartMode.Manual => "demand",
            StartMode.Disabled => "disabled",
            _ => "demand",
        };
        try
        {
            // ВАЖНО: синтаксис sc — "start= demand" (пробел ПОСЛЕ '='). Токенизируется как два аргумента.
            var psi = new ProcessStartInfo("sc.exe", $"config \"{serviceName}\" start= {arg}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            if (p.ExitCode != 0)
            {
                log?.Invoke($"sc config {serviceName} start= {arg}: exit {p.ExitCode} {o.Trim()} {e.Trim()}".Trim());
                return false;
            }
            return true;
        }
        catch (Exception ex) { log?.Invoke($"SetStartMode {serviceName}: {ex.Message}"); return false; }
    }

    /// <summary>Запустить и дождаться Running (или таймаут). true, если Running.</summary>
    public static bool Start(string serviceName, TimeSpan timeout)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running) return true;
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            sc.Start();
        return WaitFor(sc, ServiceControllerStatus.Running, timeout);
    }

    /// <summary>Остановить и дождаться Stopped (или таймаут). true, если Stopped.</summary>
    public static bool Stop(string serviceName, TimeSpan timeout)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Stopped) return true;
        if (sc.CanStop) sc.Stop();
        return WaitFor(sc, ServiceControllerStatus.Stopped, timeout);
    }

    private static bool WaitFor(ServiceController sc, ServiceControllerStatus target, TimeSpan timeout)
    {
        try
        {
            sc.WaitForStatus(target, timeout);
            return true;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            sc.Refresh();
            return sc.Status == target;
        }
    }
}
