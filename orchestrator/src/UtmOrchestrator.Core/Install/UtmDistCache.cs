using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.Versioning;

namespace UtmOrchestrator.Core.Install;

/// <summary>
/// Кэш ЧИСТОГО шаблона УТМ — из него разворачиваем каждый новый экземпляр (без
/// грязной transportDB/привязки). Кэш живёт в <c>C:\UtmOrchestrator\utm-dist\</c>
/// (agent/jre/transporter). Наполняется один раз, приоритет источников:
///   1) уже готовый кэш;
///   2) распакованный официальный установщик на dev-машине (C:\dev-tools\utm-unpacked\app);
///   3) клон существующего УТМ на машине со стрипом transportDB (чисто по софту);
/// (скачивание с fsrar + innoextract — отдельный шаг, см. Update-UTM/utm-update).
/// </summary>
[SupportedOSPlatform("windows")]
public static class UtmDistCache
{
    public static string CacheDir => Path.Combine(AppContext.BaseDirectory, "utm-dist");

    private static readonly string[] DevUnpacked =
    {
        @"C:\dev-tools\utm-unpacked\app",
    };

    /// <summary>Признак валидного шаблона — есть transporter\bin\utm.exe.</summary>
    public static bool IsValid(string dir) => File.Exists(Path.Combine(dir, "transporter", "bin", "utm.exe"));

    /// <summary>
    /// Гарантирует наличие чистого шаблона и возвращает путь к нему. existingUtmFolder —
    /// папка любого установленного УТМ для клон-сида (если нет распакованного офиц.).
    /// </summary>
    public static string? EnsureTemplate(string? existingUtmFolder, Action<string> log)
    {
        if (IsValid(CacheDir)) { log($"шаблон УТМ: кэш {CacheDir}"); return CacheDir; }

        Directory.CreateDirectory(CacheDir);

        // 2) распакованный официальный установщик (dev)
        foreach (var src in DevUnpacked)
            if (IsValid(src))
            {
                log($"шаблон УТМ: сид из распакованного {src}");
                CopyClean(src, CacheDir, log);
                return IsValid(CacheDir) ? CacheDir : null;
            }

        // 3) клон существующего УТМ со стрипом базы
        if (!string.IsNullOrEmpty(existingUtmFolder) && IsValid(existingUtmFolder))
        {
            log($"шаблон УТМ: клон из {existingUtmFolder} (со стрипом transportDB)");
            CopyClean(existingUtmFolder, CacheDir, log);
            StripInstance(CacheDir, log);
            return IsValid(CacheDir) ? CacheDir : null;
        }

        // 4) чистая машина: скачать официальный дистрибутив с fsrar + innoextract.
        log("шаблон УТМ: локального источника нет — качаю официальный дистрибутив с fsrar…");
        string? downloaded = DownloadAndUnpack(log);
        if (downloaded is not null) return downloaded;

        log("шаблон УТМ: источник не найден (не удалось скачать/распаковать официальный дистрибутив)");
        return null;
    }

    // Официальный дистрибутив УТМ (opendata FSRAR, без авторизации). Внутри zip — один
    // Inno Setup инсталлятор silent-setup-*.exe; innoextract даёт папку app/ (agent/jre/
    // transporter) = чистый шаблон. Кириллицу в имени файла кодируем сами.
    private static readonly string DistUrl =
        "https://fsrar.gov.ru/opendata/dist/"
        + Uri.EscapeDataString("установщик_транспортного_модуля_версия_4.2.0_для_Windows.zip");

    private static string? DownloadAndUnpack(Action<string> log)
    {
        string? inno = FindInnoextract();
        if (inno is null) { log("innoextract не найден (нет tools\\innoextract.exe) — распаковать дистрибутив нечем"); return null; }

        string tmp = Path.Combine(Path.GetTempPath(), "utm-dist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string zip = Path.Combine(tmp, "dist.zip");
            log($"скачивание дистрибутива: {DistUrl}");
            // Дефолтный клиент уважает системный прокси (fsrar в фильтрованных сетях — через него).
            // UA обязателен: fsrar отдаёт 403 на пустой/«curl» User-Agent (проверено).
            using var h = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) UtmOrchestrator");
            using (var resp = h.GetAsync(DistUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                using var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using var dst = File.Create(zip);
                src.CopyTo(dst);
            }
            log($"скачано: {new FileInfo(zip).Length / 1_048_576} МБ, распаковываю zip");

            string exeDir = Path.Combine(tmp, "exe");
            ZipFile.ExtractToDirectory(zip, exeDir);
            string? installer = Directory.EnumerateFiles(exeDir, "*.exe").FirstOrDefault();
            if (installer is null) { log("в дистрибутиве нет установщика .exe"); return null; }

            log($"innoextract {Path.GetFileName(installer)} (~30с)…");
            string outDir = Path.Combine(tmp, "out");
            int rc = RunInnoextract(inno, installer, outDir, log);
            string app = Path.Combine(outDir, "app");
            if (!IsValid(app)) { log($"innoextract: нет валидного app (rc={rc})"); return null; }

            log("шаблон УТМ: копирую распакованное в кэш");
            CopyClean(app, CacheDir, log);
            return IsValid(CacheDir) ? CacheDir : null;
        }
        catch (Exception e) { log("скачивание/распаковка дистрибутива: " + e.Message); return null; }
        finally { try { Directory.Delete(tmp, recursive: true); } catch { } }
    }

    private static string? FindInnoextract()
    {
        foreach (var p in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "tools", "innoextract.exe"),
                     @"C:\dev-tools\innoextract\innoextract.exe",   // dev-машина
                 })
            if (File.Exists(p)) return p;
        return null;
    }

    private static int RunInnoextract(string inno, string installer, string outDir, Action<string> log)
    {
        Directory.CreateDirectory(outDir);
        var psi = new ProcessStartInfo(inno, $"-e -d \"{outDir}\" \"{installer}\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(300_000);
        if (!string.IsNullOrWhiteSpace(err)) log("innoextract: " + err.Trim());
        return p.ExitCode;
    }

    // Копирует папку УТМ, пропуская изменяемые данные (база/логи) — в кэш кладём чистое.
    private static void CopyClean(string src, string dst, Action<string> log)
    {
        int files = 0;
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, dir);
            if (IsData(rel)) continue;
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file);
            if (IsData(rel)) continue;
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try { File.Copy(file, target, overwrite: true); files++; } catch { }
        }
        log($"шаблон: скопировано файлов {files}");
    }

    // Данные экземпляра (не софт) — не переносим в чистый шаблон.
    private static bool IsData(string rel)
    {
        string r = rel.Replace('/', '\\').ToLowerInvariant();
        return r.StartsWith(@"transporter\transportdb")
            || r.StartsWith(@"transporter\l")           // логи
            || r.StartsWith(@"agent\l");
    }

    // Стрип на всякий случай, если что-то от экземпляра проникло.
    private static void StripInstance(string dir, Action<string> log)
    {
        foreach (var sub in new[] { @"transporter\transportDB", @"transporter\l", @"agent\l" })
        {
            string p = Path.Combine(dir, sub);
            try { if (Directory.Exists(p)) { Directory.Delete(p, true); log($"стрип: {sub}"); } } catch { }
        }
    }
}
