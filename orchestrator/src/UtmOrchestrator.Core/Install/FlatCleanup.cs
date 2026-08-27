namespace UtmOrchestrator.Core.Install;

/// <summary>
/// Остатки прошлой ПЛОСКОЙ раскладки в корне установки после перехода на bin.
/// (exe/dll/pdb/…, wwwroot\, локализации, utm-dist\ и т.п. рядом с новым bin\).
/// Те же правила, что у migrate-to-bin.ps1 -Cleanup. Показываем/чистим ТОЛЬКО когда
/// уже bin-раскладка (на рабочей плоской установке это не «остатки», а сама программа).
/// </summary>
public static class FlatCleanup
{
    private static readonly string[] KeepDirs = { "bin", "data", "utms", "cache", "transfer" };
    private static readonly string[] JunkExt = { ".dll", ".exe", ".pdb", ".json", ".config", ".xml" };

    /// <summary>Мы уже на bin-раскладке? (иначе корень — это рабочая плоская установка).</summary>
    public static bool IsBinLayout()
        => File.Exists(Path.Combine(AppPaths.Root, "bin", "app", "UtmOrchestrator.Service.dll"));

    /// <summary>Список путей-остатков в корне (файлы старой раскладки + чужие папки). Пусто, если чистить нечего.</summary>
    public static List<string> Detect()
    {
        var res = new List<string>();
        if (!IsBinLayout()) return res;
        string root = AppPaths.Root;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root))
            {
                string name = Path.GetFileName(f);
                if (string.Equals(name, "runtime.key", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "appsettings.json", StringComparison.OrdinalIgnoreCase)) continue;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".ps1") continue;
                if (Array.IndexOf(JunkExt, ext) >= 0) res.Add(f);
            }
            foreach (var d in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(d);
                if (Array.FindIndex(KeepDirs, k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) < 0)
                    res.Add(d);
            }
        }
        catch { /* корень недоступен — считаем, что чистить нечего */ }
        return res;
    }

    /// <summary>Удалить остатки. Возвращает (удалено, ошибок). Занятые файлы пропускаются.</summary>
    public static (int deleted, int failed) Clean(Action<string>? log = null)
    {
        int del = 0, fail = 0;
        foreach (var p in Detect())
        {
            try
            {
                if (Directory.Exists(p)) Directory.Delete(p, true);
                else File.Delete(p);
                del++;
            }
            catch (Exception e) { fail++; log?.Invoke($"cleanup-flat: не удалил {p}: {e.Message}"); }
        }
        return (del, fail);
    }
}
