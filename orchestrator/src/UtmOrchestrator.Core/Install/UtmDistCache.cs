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
    public static string CacheDir => Path.Combine(AppPaths.CacheDir, "utm-dist");

    private static readonly string[] DevUnpacked =
    {
        @"C:\dev-tools\utm-unpacked\app",
    };

    /// <summary>
    /// Признак ПОЛНОГО шаблона: есть и transporter\bin\utm.exe, и JRE (jvm.dll) —
    /// без JRE служба Transport не стартует. Проверяем оба, чтобы отвергнуть неполную
    /// распаковку (иначе развернём УТМ, который не поднимается).
    /// </summary>
    public static bool IsValid(string dir)
    {
        string lib = Path.Combine(dir, "transporter", "lib");
        return File.Exists(Path.Combine(dir, "transporter", "bin", "utm.exe"))
            && File.Exists(Path.Combine(dir, "jre", "bin", "client", "jvm.dll"))
            // Библиотека (classpath) должна быть непустой — иначе NoClassDefFoundError.
            && Directory.Exists(lib)
            && Directory.EnumerateFiles(lib, "*.jar").Any();
    }

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
            if (!DownloadComplete(DistUrl, zip, log))
            { log("не удалось скачать полный дистрибутив (после повторов)"); return null; }

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

    // Скачивание с проверкой полноты (размер == Content-Length) и повторами: .NET может
    // молча оборвать поток на флаки-сети, а обрезанный exe → неполная распаковка (без JRE).
    private static bool DownloadComplete(string url, string dest, Action<string> log)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            long expected = 0, got = 0;
            try
            {
                // Дефолтный клиент уважает системный прокси; UA обязателен (fsrar 403 на пустой/curl).
                using var h = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
                h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) UtmOrchestrator");
                log($"скачивание дистрибутива (попытка {attempt}): {url}");
                using (var resp = h.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    expected = resp.Content.Headers.ContentLength ?? 0;
                    using var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                    using var dst = File.Create(dest);
                    src.CopyTo(dst);
                }
                got = new FileInfo(dest).Length;
                if (expected <= 0 || got >= expected)
                {
                    log($"скачано: {got / 1_048_576} МБ" + (expected > 0 ? $" из {expected / 1_048_576} МБ ✓" : ""));
                    return true;
                }
                log($"скачивание неполное: {got}/{expected} байт — повтор");
            }
            catch (Exception e) { log($"скачивание (попытка {attempt}): {e.Message}"); }
            try { File.Delete(dest); } catch { }
        }
        return false;
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
        string outp = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(300_000);
        // Логируем и stdout-хвост, и stderr, и код — чтобы видеть, обрывается ли распаковка.
        var tail = outp.Replace("\r", "").Split('\n').Where(l => l.Length > 0).TakeLast(3);
        if (tail.Any()) log("innoextract stdout: …" + string.Join(" | ", tail));
        if (!string.IsNullOrWhiteSpace(err)) log("innoextract stderr: " + err.Trim());
        int extracted = 0;
        try { extracted = Directory.EnumerateFiles(Path.Combine(outDir, "app"), "*", SearchOption.AllDirectories).Count(); } catch { }
        log($"innoextract: exit {p.ExitCode}, распаковано в app: {extracted} файлов");
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
    // ВНИМАНИЕ: точное совпадение папки или её содержимого (с завершающим '\'), иначе
    // "transporter\l" по StartsWith задевал бы "transporter\LIB" — и выкидывал все jar-ы
    // библиотеки (classpath пустел → NoClassDefFoundError, УТМ не стартовал).
    private static readonly string[] DataDirs =
        { @"transporter\transportdb", @"transporter\l", @"agent\l" };

    private static bool IsData(string rel)
    {
        string r = rel.Replace('/', '\\').ToLowerInvariant().TrimEnd('\\');
        foreach (var d in DataDirs)
            if (r == d || r.StartsWith(d + @"\", StringComparison.Ordinal)) return true;
        return false;
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
