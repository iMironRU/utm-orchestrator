using System.Diagnostics;
using System.Runtime.Versioning;

namespace UtmOrchestrator.Core.Firewall;

/// <summary>
/// Управляет собственными правилами брандмауэра оркестратора — по одному на порт УТМ
/// (имя <c>UTM-Orchestrator-{port}</c>). Выполняется службой (LocalSystem = админ),
/// поэтому UAC/трей не нужны. Открыть = создать/включить правило; закрыть = отключить
/// (правило остаётся, легко включить обратно). Подсветку статуса даёт
/// <see cref="FirewallInspector"/>, который читает ЛЮБОЕ числовое правило (включая
/// старое общее «UTM» 8080-8085), а не только наши.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FirewallManager
{
    public static string RuleName(int port) => $"UTM-Orchestrator-{port}";

    /// <summary>Открыть (open=true) или закрыть (open=false) TCP-порт во входящих.</summary>
    public static bool SetPort(int port, bool open, Action<string>? log = null)
    {
        if (port <= 0 || port > 65535) return false;
        string name = RuleName(port);
        bool exists = RuleExists(name);

        if (open)
        {
            if (!exists)
                Netsh($"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={port} profile=any", log);
            else
                Netsh($"advfirewall firewall set rule name=\"{name}\" new enable=yes", log);
        }
        else
        {
            if (exists)
                Netsh($"advfirewall firewall set rule name=\"{name}\" new enable=no", log);
            // нет нашего правила и просят закрыть — ничего не делаем (порт и так не открыт нами)
        }

        FirewallInspector.Invalidate();
        return true;
    }

    /// <summary>Переименовать/перенести правило при смене порта: удалить старое, создать новое.</summary>
    public static void MovePort(int oldPort, int newPort, bool open, Action<string>? log = null)
    {
        DeleteRule(oldPort, log);
        if (open) SetPort(newPort, true, log);
        FirewallInspector.Invalidate();
    }

    public static void DeleteRule(int port, Action<string>? log = null)
    {
        string name = RuleName(port);
        if (RuleExists(name))
            Netsh($"advfirewall firewall delete rule name=\"{name}\"", log);
        FirewallInspector.Invalidate();
    }

    private static bool RuleExists(string name)
        => Netsh($"advfirewall firewall show rule name=\"{name}\"", null) == 0;

    private static int Netsh(string args, Action<string>? log)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
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
            log?.Invoke($"netsh {args} -> exit {p.ExitCode}");
            return p.ExitCode;
        }
        catch (Exception e)
        {
            log?.Invoke($"netsh {args} -> ОШИБКА {e.Message}");
            return -1;
        }
    }
}
