using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using UtmOrchestrator.Core.Install;
using UtmOrchestrator.Core.Recovery;
using UtmOrchestrator.Core.Services;
using UtmOrchestrator.Core.State;

namespace UtmOrchestrator.Core.Transfer;

/// <summary>
/// Перенос УТМ целиком (с софтом, базой и службой) с компьютера на компьютер через
/// файл-бандл. ЭКСПОРТ (сторона-источник): останавливает службу (Derby-база requires
/// остановки), собирает бандл — вся папка УТМ + манифест + выгрузка procrun-ключа
/// реестра + ImagePath службы — и снова запускает службу (источник не разрушаем).
/// ИМПОРТ (сторона-приёмник) — см. Import ниже.
///
/// Физический токен переставляет человек; здесь только данные/софт/служба.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UtmTransfer
{
    public sealed record TransferManifest(
        string ServiceName,
        int Port,
        string? Fsrar,
        string? TokenSerial,
        string? ReaderName,
        string SourceFolderPath,
        string? UtmVersion,
        string ServiceImagePath,
        int ServiceStartType,
        string ExportedAtUtc,
        string OrchestratorVersion,
        // Кастомная «подпись» УТМ (имя точки), заданная пользователем. Едет в бандле,
        // чтобы на приёмнике не переподписывать вручную. По умолчанию null (старые бандлы).
        string? DisplayName = null);

    public sealed record ExportResult(bool Success, string Message, string? BundlePath);

    /// <summary>Результат импорта: развёрнутый инстанс (ещё не поднятый) + подпись/серийник
    /// для восстановления имени и обучения кэшей на приёмнике.</summary>
    public sealed record ImportResult(
        bool Success, string Message, UtmInstance? Instance, string? DisplayName, string? TokenSerial);

    private const string ManifestEntry = "manifest.json";
    private const string ProcrunRegEntry = "procrun.reg";
    private const string UtmFolderPrefix = "utm/";

    /// <summary>
    /// Экспортирует УТМ в zip-бандл в <paramref name="exportsDir"/>. Останавливает
    /// службу на время упаковки и запускает обратно. Источник остаётся рабочим.
    /// </summary>
    public static ExportResult Export(
        UtmInstance inst, IReadOnlyList<string> allReaders, string? utmVersion,
        string exportsDir, Action<string> log, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(inst.FolderPath) || !Directory.Exists(inst.FolderPath))
            return new(false, $"папка УТМ не найдена: {inst.FolderPath}", null);

        string svc = inst.ServiceName;
        string imagePath = ReadServiceImagePath(svc) ?? "";
        int startType = ReadServiceStartType(svc);
        string? procrunReg = ExportProcrunRegistry(svc, log);

        Directory.CreateDirectory(exportsDir);
        string safeFsrar = string.IsNullOrEmpty(inst.Fsrar()) ? svc : inst.Fsrar()!;
        string bundlePath = Path.Combine(exportsDir,
            $"UTM-export-{safeFsrar}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");

        bool wasRunning = ServiceControl.IsRunning(svc);
        try
        {
            if (wasRunning)
            {
                log($"Останавливаю {svc} для консистентной выгрузки базы…");
                ServiceControl.Stop(svc, TimeSpan.FromSeconds(60));
            }

            var manifest = new TransferManifest(
                svc, inst.Port, inst.ExpectedFsrar, inst.TokenSerial, inst.ReaderName,
                inst.FolderPath, utmVersion, imagePath, startType,
                DateTime.UtcNow.ToString("o"), AppInfo.Version, displayName);

            // Пишем в .tmp и переименовываем по готовности — чтобы список/скачивание
            // не подхватили недописанный бандл.
            string tmpPath = bundlePath + ".tmp";
            if (File.Exists(tmpPath)) File.Delete(tmpPath);

            log($"Упаковываю папку УТМ ({inst.FolderPath}) в бандл…");
            using (var zip = ZipFile.Open(tmpPath, ZipArchiveMode.Create))
            {
                // манифест
                WriteTextEntry(zip, ManifestEntry,
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                // procrun-реестр (может быть null, если ключ не найден)
                if (procrunReg is not null)
                    WriteTextEntry(zip, ProcrunRegEntry, procrunReg);
                // вся папка УТМ под utm/
                AddDirectory(zip, inst.FolderPath, UtmFolderPrefix, log);
            }
            File.Move(tmpPath, bundlePath);

            long sizeMb = new FileInfo(bundlePath).Length / 1_048_576;
            log($"Бандл готов: {bundlePath} ({sizeMb} МБ)");
            return new(true, $"бандл {sizeMb} МБ", bundlePath);
        }
        catch (Exception e)
        {
            try { if (File.Exists(bundlePath)) File.Delete(bundlePath); } catch { }
            try { if (File.Exists(bundlePath + ".tmp")) File.Delete(bundlePath + ".tmp"); } catch { }
            return new(false, "ошибка экспорта: " + e.Message, null);
        }
        finally
        {
            if (wasRunning)
            {
                // Возвращаем УТМ той же introduce-хореографией, что и рестарт: простой
                // Start прочитал бы slot0 и мог привязать ЧУЖОЙ токен. Источник не тронут.
                log($"Возвращаю {svc} (introduce, чтобы привязался свой токен)…");
                try
                {
                    var target = new BootBringUp.Target(svc, inst.Port, inst.TokenSerial ?? "", inst.ExpectedFsrar, inst.ReaderName);
                    BootBringUp.RestartOne(target, allReaders, log);
                }
                catch (Exception e) { log($"не удалось вернуть {svc}: {e.Message}"); }
            }
        }
    }

    /// <summary>
    /// ИМПОРТ (сторона-приёмник): разворачивает бандл на этой машине. Читает манифест,
    /// подбирает папку/службу/порт (предпочитает исходные — меньше расхождений путей; при
    /// коллизии берёт свободные), распаковывает папку УТМ, при смене порта правит конфиги,
    /// регистрирует procrun-службу и открывает порт в файрволе. Службу НЕ поднимает —
    /// токен может быть ещё не подключён; привязка делается серийным подъёмом
    /// (BootBringUp.Apply) отдельным шагом «Привязать все токены».
    /// Идентификатор токена (серийник) — стабилен и на новой машине, поэтому это
    /// первичный ключ; имя ридера из бандла — только подсказка (на приёмнике оно другое).
    /// </summary>
    public static ImportResult Import(
        string bundlePath, IReadOnlyList<UtmInstance> existing, Action<string> log)
    {
        if (!File.Exists(bundlePath)) return new(false, $"бандл не найден: {bundlePath}", null, null, null);

        TransferManifest? manifest;
        try
        {
            using var zr = ZipFile.OpenRead(bundlePath);
            var mEntry = zr.GetEntry(ManifestEntry);
            if (mEntry is null)
                return new(false, "в бандле нет manifest.json — это не бандл переноса", null, null, null);
            using var s = mEntry.Open();
            using var sr = new StreamReader(s, Encoding.UTF8);
            manifest = JsonSerializer.Deserialize<TransferManifest>(sr.ReadToEnd());
        }
        catch (Exception e) { return new(false, "не удалось прочитать манифест: " + e.Message, null, null, null); }
        if (manifest is null) return new(false, "манифест пуст/повреждён", null, null, null);

        // Уже импортирован? Ключ — серийник токена (стабилен между машинами).
        if (!string.IsNullOrEmpty(manifest.TokenSerial) &&
            existing.Any(i => string.Equals(i.TokenSerial, manifest.TokenSerial, StringComparison.OrdinalIgnoreCase)))
            return new(false, $"токен {manifest.TokenSerial} уже привязан к УТМ на этой машине",
                null, manifest.DisplayName, manifest.TokenSerial);

        string folder = ChooseFolder(manifest.SourceFolderPath);
        string service = ChooseService(manifest.ServiceName, existing);
        int port = ChoosePort(manifest.Port, existing);
        log($"импорт: серийник {manifest.TokenSerial}, папка {folder}, служба {service}, порт {port} " +
            $"(из бандла: {manifest.ServiceName}/{manifest.Port}/{manifest.SourceFolderPath})");

        // Распаковка utm/ → выбранная папка.
        try
        {
            Directory.CreateDirectory(folder);
            using var zr = ZipFile.OpenRead(bundlePath);
            int files = 0;
            foreach (var entry in zr.Entries)
            {
                if (!entry.FullName.StartsWith(UtmFolderPrefix, StringComparison.Ordinal)) continue;
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue; // директория
                string rel = entry.FullName.Substring(UtmFolderPrefix.Length).Replace('/', Path.DirectorySeparatorChar);
                string dest = Path.Combine(folder, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                files++;
            }
            log($"распаковано файлов УТМ: {files}");
            if (files == 0) return new(false, "в бандле нет файлов УТМ (utm/…)", null, manifest.DisplayName, manifest.TokenSerial);
        }
        catch (Exception e) { return new(false, "распаковка: " + e.Message, null, manifest.DisplayName, manifest.TokenSerial); }

        // Порт в конфиги (при совпадении с исходным — безвредно перезапишет тем же значением).
        SetPortKey(Path.Combine(folder, "transporter", "conf", "transport.properties"), "web.server.port", port, log);
        SetPortKey(Path.Combine(folder, "agent", "conf", "agent.properties"), "utm.port", port, log);

        // Регистрация службы штатным install.bat развёрнутой папки (относительные пути).
        if (!ProcrunService.Register(service, folder, log))
            return new(false, "не удалось зарегистрировать службу (procrun)", null, manifest.DisplayName, manifest.TokenSerial);

        // Файрвол на порт (как при обычной установке).
        try { UtmOrchestrator.Core.Firewall.FirewallManager.SetPort(port, true, log); }
        catch (Exception e) { log("файрвол: " + e.Message); }

        var inst = new UtmInstance
        {
            Port = port,
            ServiceName = service,
            FolderPath = folder,
            TokenSerial = manifest.TokenSerial,
            ExpectedFsrar = manifest.Fsrar,
            ReaderName = manifest.ReaderName, // подсказка; уточнится при серийной привязке
        };
        log($"импорт УТМ развёрнут (не поднят): {service} :{port} — привяжется по серийнику при «Привязать все токены»");
        return new(true, $"УТМ {service} импортирован на порт {port}", inst, manifest.DisplayName, manifest.TokenSerial);
    }

    // Папка приёмника: предпочитаем исходную (меньше расхождений путей), если свободна;
    // иначе — следующая свободная C:\UTM_N.
    private static string ChooseFolder(string? sourceFolder)
    {
        if (!string.IsNullOrWhiteSpace(sourceFolder) && !Directory.Exists(sourceFolder))
            return sourceFolder!;
        int idx = 2;
        string folder;
        do { folder = idx == 1 ? @"C:\UTM" : $@"C:\UTM_{idx}"; idx++; } while (Directory.Exists(folder));
        return folder;
    }

    // Имя службы: предпочитаем исходное, если не занято и не установлено; иначе Transport/TransportN.
    private static string ChooseService(string sourceService, IReadOnlyList<UtmInstance> existing)
    {
        var used = existing.Select(i => i.ServiceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(sourceService) && !used.Contains(sourceService)
            && ServiceControl.GetState(sourceService) == ServiceState.NotInstalled)
            return sourceService;
        string svcBase = "Transport";
        string service = svcBase;
        int s = 2;
        while (used.Contains(service) || ServiceControl.GetState(service) != ServiceState.NotInstalled)
            service = svcBase + s++;
        return service;
    }

    // Порт: предпочитаем исходный, если свободен; иначе следующий свободный.
    private static int ChoosePort(int desired, IReadOnlyList<UtmInstance> existing)
    {
        var used = existing.Where(i => i.Port > 0).Select(i => i.Port).ToHashSet();
        int port = desired > 0 ? desired : 8080;
        while (used.Contains(port) || PortInUse(port)) port++;
        return port;
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

    // Проставить целочисленный ключ в .properties (заменить или дописать).
    private static void SetPortKey(string path, string key, int value, Action<string> log)
    {
        if (!File.Exists(path)) { log($"нет {path} — пропуск {key}"); return; }
        string text = File.ReadAllText(path);
        var rx = new System.Text.RegularExpressions.Regex(@"(?m)^(\s*" +
            System.Text.RegularExpressions.Regex.Escape(key) + @"\s*=).*$");
        text = rx.IsMatch(text)
            ? rx.Replace(text, "${1}" + value, 1)
            : text.TrimEnd() + $"\n{key}={value}\n";
        File.WriteAllText(path, text);
        log($"{Path.GetFileName(path)}: {key}={value}");
    }

    // --- вспомогательное ---

    private static void WriteTextEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void AddDirectory(ZipArchive zip, string sourceDir, string prefix, Action<string> log)
    {
        int files = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            try
            {
                zip.CreateEntryFromFile(file, prefix + rel, CompressionLevel.Fastest);
                files++;
            }
            catch (IOException)
            {
                // файл занят (например, db.lck при не до конца остановленной службе) — пропускаем
                log($"пропущен занятый файл: {rel}");
            }
        }
        log($"добавлено файлов: {files}");
    }

    private static string? ReadServiceImagePath(string service)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
        return key?.GetValue("ImagePath") as string;
    }

    private static int ReadServiceStartType(string service)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
        return key?.GetValue("Start") is int s ? s : 2; // 2 = Automatic по умолчанию
    }

    // Выгружает ветку procrun выбранной службы в текст .reg (reg export). Возвращает
    // содержимое или null, если ключ не найден.
    private static string? ExportProcrunRegistry(string service, Action<string> log)
    {
        foreach (var root in new[]
                 {
                     $@"HKLM\SOFTWARE\WOW6432Node\Apache Software Foundation\Procrun 2.0\{service}",
                     $@"HKLM\SOFTWARE\Apache Software Foundation\Procrun 2.0\{service}",
                 })
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"procrun-{service}-{Guid.NewGuid():N}.reg");
            try
            {
                var psi = new ProcessStartInfo("reg", $"export \"{root}\" \"{tmp}\" /y")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi)!;
                p.WaitForExit(15000);
                if (p.ExitCode == 0 && File.Exists(tmp))
                {
                    string content = File.ReadAllText(tmp, Encoding.Unicode);
                    log($"procrun-ключ выгружен: {root}");
                    return content;
                }
            }
            catch (Exception e) { log($"reg export {root}: {e.Message}"); }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
        log("procrun-ключ не найден (служба не procrun?) — импорт восстановит службу по манифесту");
        return null;
    }
}

internal static class UtmInstanceTransferExt
{
    // Короткий доступ к ФСРАР для имени файла.
    public static string? Fsrar(this UtmInstance i) => i.ExpectedFsrar;
}
