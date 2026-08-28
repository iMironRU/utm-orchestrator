using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace UtmOrchestrator.Core.Install;

/// <summary>
/// Обновление самого УТМ (transporter+agent) из ОФИЦИАЛЬНОГО дистрибутива fsrar.gov.ru.
/// Логика diff-апдейта портирована с проверенного Update-UTM.ps1, но своя (наши тонкости:
/// introduce-ребинд токена, мультимашина, бэкап/откат, прогресс — делает вызывающий код).
///
/// ВАЖНО: fsrar.gov.ru — росс. госсайт, качать НАПРЯМУЮ (UseProxy=false). Через обход-прокси
/// (для GitHub) он отдаёт 403. Служба-LocalSystem как раз ходит direct — то, что нужно.
///
/// Что СОХРАНЯЕМ (не перезаписываем): база Derby и конфиг — l\ xml\ conf\ transportDB\log\
/// transportDB\seg0\, transportDB\service.properties, файлы .log/.gz/.lck (в каждом компоненте).
/// Что ПОЛНОСТЬЮ чистим: spa\ (хэш-именованный фронт-бандл). Остальное — заменяем/добавляем,
/// устаревшие версии jar (тот же stem, другая версия) — удаляем.
/// </summary>
public static class UtmUpdater
{
    // Официальный дистрибутив УТМ (opendata — публично, без КЭП). ФСРАР обновляет файл по тому же URL.
    public const string UtmUrl =
        "https://fsrar.gov.ru/opendata/dist/%D1%83%D1%81%D1%82%D0%B0%D0%BD%D0%BE%D0%B2%D1%89%D0%B8%D0%BA_%D1%82%D1%80%D0%B0%D0%BD%D1%81%D0%BF%D0%BE%D1%80%D1%82%D0%BD%D0%BE%D0%B3%D0%BE_%D0%BC%D0%BE%D0%B4%D1%83%D0%BB%D1%8F_%D0%B2%D0%B5%D1%80%D1%81%D0%B8%D1%8F_4.2.0_%D0%B4%D0%BB%D1%8F_Windows.zip";

    private static readonly string[] Components      = { "transporter", "agent" };
    private static readonly string[] SkipSubPrefixes = { @"l\", @"xml\", @"conf\", @"transportDB\log\", @"transportDB\seg0\" };
    private static readonly string[] SkipSubExact    = { @"transportDB\service.properties" };
    private static readonly HashSet<string> SkipExt  = new(StringComparer.OrdinalIgnoreCase) { ".log", ".gz", ".lck" };
    private static readonly string[] WipeSubDirs     = { @"spa\" };

    public sealed record ApplyResult(int Add, int Update, int Delete, int Wipe, int Errors, IReadOnlyList<string> Added)
    {
        public int Total => Add + Update + Delete + Wipe;
    }

    /// <summary>
    /// Откат апдейта: удалить ДОБАВЛЕННЫЕ файлы + вернуть оригиналы из бэкапа поверх папки.
    /// Полностью возвращает УТМ к состоянию до Apply (при сбое подъёма после обновления).
    /// </summary>
    public static void Restore(string oldRoot, string backupDir, IReadOnlyList<string> added, Action<string> log)
    {
        foreach (var rel in added)
        {
            try { var f = Path.Combine(oldRoot, rel); if (File.Exists(f)) File.Delete(f); }
            catch (Exception e) { log($"откат: не удалил добавленный {rel}: {e.Message}"); }
        }
        if (Directory.Exists(backupDir))
        {
            foreach (var f in Directory.EnumerateFiles(backupDir, "*", SearchOption.AllDirectories))
            {
                string rel = RelPath(backupDir, f);
                try { var dest = Path.Combine(oldRoot, rel); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(f, dest, true); }
                catch (Exception e) { log($"откат: не вернул {rel}: {e.Message}"); }
            }
        }
        log("откат из бэкапа выполнен");
    }

    /// <summary>Путь к innoextract.exe рядом с нашим кодом (bin\app\tools\innoextract.exe).</summary>
    public static string InnoextractPath => Path.Combine(AppContext.BaseDirectory, "tools", "innoextract.exe");

    // Метка версии на сервере fsrar (Last-Modified + размер) — для сверки с кэшем. null если недоступно.
    private static string? HeadTag(Action<string> log)
    {
        try
        {
            using var h = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            using var req = new HttpRequestMessage(HttpMethod.Head, UtmUrl);
            using var resp = h.Send(req);
            if (!resp.IsSuccessStatusCode) return null;
            string lm = resp.Content.Headers.LastModified?.ToString("O") ?? "";
            long len = resp.Content.Headers.ContentLength ?? 0;
            return lm + "|" + len;
        }
        catch (Exception e) { log($"HEAD fsrar: {e.Message}"); return null; }
    }

    /// <summary>
    /// Скачать дистрибутив с fsrar (НАПРЯМУЮ) + распаковать zip + innoextract → путь к шаблону (…\app).
    /// null при ошибке.
    /// </summary>
    public static string? DownloadAndExtract(string workDir, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(workDir);
            string inno = InnoextractPath;
            if (!File.Exists(inno)) { log($"innoextract не найден: {inno}"); return null; }

            string outDir = Path.Combine(workDir, "out");
            string appCached = Path.Combine(outDir, "app");
            string marker = Path.Combine(outDir, "source.txt");

            // Сверка кэша: HEAD → Last-Modified+размер. Совпало с сохранённым и шаблон на месте —
            // не качаем 150 МБ и не распаковываем заново.
            string? remoteTag = HeadTag(log);
            if (remoteTag is not null && Directory.Exists(Path.Combine(appCached, "transporter"))
                && File.Exists(marker) && File.ReadAllText(marker).Trim() == remoteTag)
            {
                log("кэш дистрибутива актуален — скачивание не требуется");
                return appCached;
            }

            string zip = Path.Combine(workDir, "installer.zip");
            log("качаю дистрибутив УТМ с fsrar.gov.ru (напрямую, без прокси)…");
            using (var h = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = true })
            { Timeout = TimeSpan.FromMinutes(30) })
            {
                h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                using var resp = h.GetAsync(UtmUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                using var src = resp.Content.ReadAsStream();
                using var dst = File.Create(zip);
                src.CopyTo(dst);
            }
            log($"скачано: {new FileInfo(zip).Length / 1024 / 1024} МБ. Распаковываю zip…");

            string zdir = Path.Combine(workDir, "zip");
            if (Directory.Exists(zdir)) Directory.Delete(zdir, true);
            ZipFile.ExtractToDirectory(zip, zdir);

            string? exe = Directory.EnumerateFiles(zdir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe is null) { log("установщик .exe внутри zip не найден"); return null; }

            log("извлекаю innoextract…");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            var psi = new ProcessStartInfo(inno, $"--output-dir \"{outDir}\" \"{exe}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi)!)
            {
                p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(300000);
                if (p.ExitCode != 0) { log($"innoextract код {p.ExitCode}: {err.Trim()}"); return null; }
            }

            string app = Path.Combine(outDir, "app");
            if (!Directory.Exists(app)) { log("папка app не найдена после распаковки"); return null; }

            // Версия из имени установщика (silent-setup-4.2.0-b2698.exe → 4.2.0-b2698) — для показа «доступно».
            try
            {
                var m = Regex.Match(Path.GetFileName(exe), @"(\d+\.\d+\.\d+(?:-b\d+)?)");
                if (m.Success) File.WriteAllText(Path.Combine(outDir, "installer-version.txt"), m.Value);
            }
            catch { }

            try { if (remoteTag is not null) File.WriteAllText(marker, remoteTag); } catch { }
            try { File.Delete(zip); Directory.Delete(zdir, true); } catch { }
            log($"шаблон готов: {app}");
            return app;
        }
        catch (Exception e) { log($"скачивание/распаковка УТМ: СБОЙ — {e.Message}"); return null; }
    }

    /// <summary>Версия сборки УТМ в папке (из SPA-бандла), напр. «4.27.668».</summary>
    public static string? Version(string folder) => UtmOrchestrator.Core.Diagnostics.UtmBuildVersion.Read(folder);

    /// <summary>
    /// Дата сборки кода УТМ = максимальный mtime jar-файлов в transporter\lib. Сравнимо
    /// между установщиком и установленным (в отличие от несравнимых номеров версий) —
    /// на этом строим «не даунгрейдить»: обновляем, только если официальный НОВЕЕ.
    /// </summary>
    public static DateTime? BuildDate(string folder)
    {
        try
        {
            string lib = Path.Combine(folder, "transporter", "lib");
            if (!Directory.Exists(lib)) return null;
            DateTime? max = null;
            foreach (var f in Directory.EnumerateFiles(lib, "*.jar"))
            {
                var t = File.GetLastWriteTime(f);
                if (max is null || t > max.Value) max = t;
            }
            return max;
        }
        catch { return null; }
    }

    /// <summary>Версия скачанного установщика (из имени: 4.2.0-b2698). null если не качали.</summary>
    public static string? AvailableVersion(string templateApp)
    {
        try
        {
            string? outDir = Path.GetDirectoryName(templateApp);
            if (outDir is null) return null;
            string f = Path.Combine(outDir, "installer-version.txt");
            return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Применить шаблон newRoot к установленному УТМ oldRoot с бэкапом в backupDir. dryRun — только план.
    /// Сохраняет базу/конфиг по skip-правилам; spa\ чистит; устаревшие jar удаляет.
    /// </summary>
    public static ApplyResult Apply(string oldRoot, string newRoot, string backupDir, bool dryRun, Action<string> log)
    {
        var skipPrefixes = new List<string>();
        var skipExact    = new List<string>();
        var wipePaths    = new List<string>();
        foreach (var c in Components)
        {
            foreach (var p in SkipSubPrefixes) skipPrefixes.Add(c + "\\" + p);
            foreach (var e in SkipSubExact)    skipExact.Add(c + "\\" + e);
            foreach (var w in WipeSubDirs)     wipePaths.Add(c + "\\" + w);
        }

        var oldMap = BuildMap(oldRoot);
        var newMap = BuildMap(newRoot);

        var planAdd    = new List<string>();
        var planUpdate = new List<string>();
        foreach (var rel in newMap.Keys)
        {
            if (ShouldSkip(rel, skipPrefixes, skipExact)) continue;
            if (!oldMap.TryGetValue(rel, out var oldF)) { planAdd.Add(rel); continue; }
            var newF = newMap[rel];
            if (oldF.Length != newF.Length) { planUpdate.Add(rel); continue; }
            if (!Sha(oldF.FullName).Equals(Sha(newF.FullName), StringComparison.OrdinalIgnoreCase)) planUpdate.Add(rel);
        }

        // Устаревшие jar: в old есть версия, которой нет в new, но stem (имя без версии) в new присутствует.
        var planDelete = new List<string>();
        foreach (var c in Components)
        {
            string oldLib = Path.Combine(oldRoot, c, "lib");
            string newLib = Path.Combine(newRoot, c, "lib");
            if (!Directory.Exists(oldLib) || !Directory.Exists(newLib)) continue;
            var newJars  = Directory.EnumerateFiles(newLib, "*.jar").Select(f => Path.GetFileName(f)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newStems = newJars.Select(JarStem).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var jar in Directory.EnumerateFiles(oldLib, "*.jar"))
            {
                string name = Path.GetFileName(jar)!;
                if (newJars.Contains(name)) continue;
                if (!newStems.Contains(JarStem(name))) continue;
                planDelete.Add(RelPath(oldRoot, jar));
            }
        }

        var planWipe = wipePaths.Where(w => Directory.Exists(Path.Combine(oldRoot, w.TrimEnd('\\')))).ToList();

        int total = planAdd.Count + planUpdate.Count + planDelete.Count + planWipe.Count;
        log($"план: ADD {planAdd.Count}, UPDATE {planUpdate.Count}, DELETE {planDelete.Count}, WIPE {planWipe.Count} (итого {total})");
        if (dryRun || total == 0) return new ApplyResult(planAdd.Count, planUpdate.Count, planDelete.Count, planWipe.Count, 0, planAdd);

        Directory.CreateDirectory(backupDir);
        int errors = 0;

        foreach (var rel in planUpdate)
        {
            try { var dest = Path.Combine(oldRoot, rel); Backup(dest, backupDir, rel); File.Copy(Path.Combine(newRoot, rel), dest, true); }
            catch (Exception e) { log($"[ERR upd] {rel}: {e.Message}"); errors++; }
        }
        foreach (var rel in planAdd)
        {
            try { var dest = Path.Combine(oldRoot, rel); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(Path.Combine(newRoot, rel), dest, true); }
            catch (Exception e) { log($"[ERR add] {rel}: {e.Message}"); errors++; }
        }
        foreach (var rel in planDelete)
        {
            try { var t = Path.Combine(oldRoot, rel); Backup(t, backupDir, rel); File.Delete(t); }
            catch (Exception e) { log($"[ERR del] {rel}: {e.Message}"); errors++; }
        }
        foreach (var w in planWipe)
        {
            try
            {
                var t = Path.Combine(oldRoot, w.TrimEnd('\\'));
                foreach (var f in Directory.EnumerateFiles(t, "*", SearchOption.AllDirectories))
                    Backup(f, backupDir, RelPath(oldRoot, f));
                Directory.Delete(t, true);
            }
            catch (Exception e) { log($"[ERR wipe] {w}: {e.Message}"); errors++; }
        }

        log($"apply готов: ADD={planAdd.Count} UPD={planUpdate.Count} DEL={planDelete.Count} WIPE={planWipe.Count} ошибок={errors}");
        return new ApplyResult(planAdd.Count, planUpdate.Count, planDelete.Count, planWipe.Count, errors, planAdd);
    }

    private static Dictionary<string, FileInfo> BuildMap(string root)
    {
        var map = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Components)
        {
            string croot = Path.Combine(root, c);
            if (!Directory.Exists(croot)) continue;
            foreach (var f in Directory.EnumerateFiles(croot, "*", SearchOption.AllDirectories))
                map[RelPath(root, f)] = new FileInfo(f);
        }
        return map;
    }

    private static bool ShouldSkip(string rel, List<string> pre, List<string> exact)
    {
        foreach (var p in pre)   if (rel.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var e in exact) if (rel.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        return SkipExt.Contains(Path.GetExtension(rel));
    }

    private static string RelPath(string baseDir, string full) => Path.GetRelativePath(baseDir, full);

    private static void Backup(string src, string backupRoot, string rel)
    {
        string dest = Path.Combine(backupRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, true);
    }

    private static string JarStem(string jarName)
    {
        string b = Path.GetFileNameWithoutExtension(jarName);
        var m = Regex.Match(b, @"^(.+?)-\d[\d.]*$");
        return m.Success ? m.Groups[1].Value : b;
    }

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }
}
