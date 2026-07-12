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

        log("шаблон УТМ: источник не найден (нужен распакованный офиц. или существующий УТМ)");
        return null;
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
