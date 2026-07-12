using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using UtmOrchestrator.Core.Firewall;
using UtmOrchestrator.Core.Recovery;
using UtmOrchestrator.Core.Services;
using UtmOrchestrator.Core.State;

namespace UtmOrchestrator.Core.Install;

/// <summary>
/// Установка НОВОГО УТМ «с нуля»: развернуть чистый шаблон в новую папку, задать порт,
/// зарегистрировать procrun-службу (клон эталонной), открыть порт в файрволе, привязать
/// токен (introduce) и запустить. Требует прав администратора (служба = LocalSystem) и
/// физически подключённого токена под нужным ридером.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UtmInstaller
{
    public sealed record Result(bool Success, string Message, UtmInstance? Instance);

    public static Result AddNew(
        string tokenSerial, string? fsrar, string readerName,
        IReadOnlyList<UtmInstance> existing, IReadOnlyList<string> allReaders,
        int? desiredPort, Action<string> log)
    {
        // 1) свободный порт, папка, имя службы
        var usedPorts = existing.Where(i => i.Port > 0).Select(i => i.Port).ToHashSet();
        int port = desiredPort ?? (usedPorts.Count > 0 ? usedPorts.Max() + 1 : 8080);
        while (usedPorts.Contains(port) || PortInUse(port)) port++;

        int idx = 2;
        string folder;
        do { folder = idx == 1 ? @"C:\UTM" : $@"C:\UTM_{idx}"; idx++; }
        while (Directory.Exists(folder));

        string svcBase = "Transport";
        string service = svcBase;
        int s = 2;
        var usedSvc = existing.Select(i => i.ServiceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (usedSvc.Contains(service) || ServiceControl.GetState(service) != ServiceState.NotInstalled)
            service = svcBase + s++;

        log($"новый УТМ: порт {port}, папка {folder}, служба {service}, ридер {readerName}");

        // 2) чистый шаблон (источник-клон — любой существующий УТМ, если есть)
        string? template = UtmDistCache.EnsureTemplate(existing.FirstOrDefault()?.FolderPath, log);
        if (template is null) return new(false, "не удалось получить чистый шаблон УТМ", null);

        // 3) развернуть шаблон в новую папку
        try { CopyDir(template, folder, log); }
        catch (Exception e) { return new(false, "копирование шаблона: " + e.Message, null); }

        // 4) порт в конфиги
        SetKey(Path.Combine(folder, "transporter", "conf", "transport.properties"), "web.server.port", port, log);
        SetKey(Path.Combine(folder, "agent", "conf", "agent.properties"), "utm.port", port, log);

        // 5) регистрация службы штатным install.bat развёрнутого шаблона
        if (!ProcrunService.Register(service, folder, log))
            return new(false, "не удалось зарегистрировать службу (procrun)", null);

        // 6) файрвол на порт
        try { FirewallManager.SetPort(port, true, log); } catch (Exception e) { log("файрвол: " + e.Message); }

        // 7) привязать токен и запустить (introduce)
        var target = new BootBringUp.Target(service, port, tokenSerial, fsrar, readerName);
        var readers = allReaders.Contains(readerName) ? allReaders : allReaders.Append(readerName).ToList();
        try { BootBringUp.RestartOne(target, readers, log); }
        catch (Exception e) { return new(false, "привязка/запуск нового УТМ: " + e.Message, null); }

        var inst = new UtmInstance
        {
            Port = port, ServiceName = service, FolderPath = folder,
            TokenSerial = tokenSerial, ExpectedFsrar = fsrar, ReaderName = readerName,
        };
        log($"новый УТМ готов: {service} :{port}");
        return new(true, $"УТМ {service} на порту {port}", inst);
    }

    private static bool PortInUse(int port)
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(ep => ep.Port == port);
        }
        catch { return false; }
    }

    private static void CopyDir(string src, string dst, Action<string> log)
    {
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        int n = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true); n++;
        }
        log($"шаблон развёрнут: {n} файлов → {dst}");
    }

    private static void SetKey(string path, string key, int value, Action<string> log)
    {
        if (!File.Exists(path)) { log($"нет {path}"); return; }
        string text = File.ReadAllText(path);
        var rx = new Regex(@"(?m)^(\s*" + Regex.Escape(key) + @"\s*=).*$");
        text = rx.IsMatch(text) ? rx.Replace(text, "${1}" + value, 1) : text.TrimEnd() + $"\n{key}={value}\n";
        File.WriteAllText(path, text);
        log($"{Path.GetFileName(path)}: {key}={value}");
    }
}
