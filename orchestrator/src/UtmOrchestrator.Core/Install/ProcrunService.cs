using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace UtmOrchestrator.Core.Install;

/// <summary>
/// Регистрирует procrun-службу УТМ его ЖЕ штатным <c>install.bat</c>
/// (transporter\bin\install.bat). Он использует относительные пути (%~dp0 / %BASEDIR%),
/// поэтому для нового экземпляра достаточно поменять ИМЯ службы (аргумент после
/// <c>install</c>) и запустить bat из новой папки — все пути (Classpath, Jvm, конфиги,
/// логи) резолвятся сами. Надёжнее ручной сборки команды //IS// или правки реестра
/// (пути там hex-кодированы). Требует прав администратора.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcrunService
{
    /// <summary>Зарегистрировать новую службу newService для УТМ в newFolder.</summary>
    public static bool Register(string newService, string newFolder, Action<string> log)
    {
        string binDir = Path.Combine(newFolder, "transporter", "bin");
        string bat = Path.Combine(binDir, "install.bat");
        if (!File.Exists(bat)) { log($"нет install.bat: {bat}"); return false; }

        // Меняем ТОЛЬКО имя службы: "utm.exe install Transport " → "... install <newService> ".
        string content = File.ReadAllText(bat);
        string modified = Regex.Replace(content, @"(utm\.exe\s+install\s+)Transport(\s)", "$1" + newService + "$2");
        if (modified == content)
            log("предупреждение: строка 'install Transport' не найдена — bat запущу как есть");

        string tmpBat = Path.Combine(binDir, "install-orch.bat");
        try
        {
            File.WriteAllText(tmpBat, modified);
            int rc = Run("cmd.exe", $"/c \"{tmpBat}\"", binDir, log);
            log($"install.bat ({newService}): exit {rc}");
            // procrun install.bat может вернуть 0 при успехе; проверим появление службы
            return rc == 0;
        }
        catch (Exception e) { log("register: " + e.Message); return false; }
        finally { try { File.Delete(tmpBat); } catch { } }
    }

    /// <summary>Снять службу штатным delete.bat (для отката/очистки).</summary>
    public static bool Unregister(string service, string folder, Action<string> log)
    {
        string binDir = Path.Combine(folder, "transporter", "bin");
        string bat = Path.Combine(binDir, "delete.bat");
        if (!File.Exists(bat)) return false;
        string modified = Regex.Replace(File.ReadAllText(bat), @"(utm\.exe\s+delete\s+)Transport(\s|$)", "$1" + service + "$2");
        string tmpBat = Path.Combine(binDir, "delete-orch.bat");
        try { File.WriteAllText(tmpBat, modified); return Run("cmd.exe", $"/c \"{tmpBat}\"", binDir, log) == 0; }
        finally { try { File.Delete(tmpBat); } catch { } }
    }

    private static int Run(string exe, string args, string workingDir, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDir,
            };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            if (!string.IsNullOrWhiteSpace(o)) log(o.Trim());
            if (!string.IsNullOrWhiteSpace(e)) log(e.Trim());
            return p.ExitCode;
        }
        catch (Exception ex) { log($"{exe} {args}: {ex.Message}"); return -1; }
    }
}
