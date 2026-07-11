using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
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
        string OrchestratorVersion);

    public sealed record ExportResult(bool Success, string Message, string? BundlePath);

    private const string ManifestEntry = "manifest.json";
    private const string ProcrunRegEntry = "procrun.reg";
    private const string UtmFolderPrefix = "utm/";

    /// <summary>
    /// Экспортирует УТМ в zip-бандл в <paramref name="exportsDir"/>. Останавливает
    /// службу на время упаковки и запускает обратно. Источник остаётся рабочим.
    /// </summary>
    public static ExportResult Export(
        UtmInstance inst, IReadOnlyList<string> allReaders, string? utmVersion,
        string exportsDir, Action<string> log)
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
                DateTime.UtcNow.ToString("o"), AppInfo.Version);

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
