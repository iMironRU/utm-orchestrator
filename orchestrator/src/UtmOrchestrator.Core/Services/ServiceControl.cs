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
