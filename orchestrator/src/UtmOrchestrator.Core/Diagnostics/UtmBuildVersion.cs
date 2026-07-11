using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace UtmOrchestrator.Core.Diagnostics;

/// <summary>
/// Точная версия СБОРКИ УТМ (напр. «4.27.668»). /api/info/list отдаёт только версию
/// формата («4.2.0»), а точная сборка зашита в SPA-бандл фронтенда УТМ
/// (<c>transporter/spa/main-es*.js</c> → <c>module.exports={&lt;key&gt;:"4.27.668"}</c>).
/// Читаем файл с диска (папка УТМ известна), кэшируем по папке — версия статична
/// на установку.
/// </summary>
public static class UtmBuildVersion
{
    private static readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Модули вида  e.exports={<key>:"X.Y.Z"}. В бандле их несколько: у библиотек ключ
    // обычно "version" ({version:"1.0.35"}), а у УТМ — минифицированный ({i8:"4.27.668"}).
    // Поэтому берём версию из модуля, чей ключ НЕ "version".
    private static readonly Regex Rx = new(
        @"exports\s*=\s*\{\s*([A-Za-z_$][\w$]*)\s*:\s*""(\d+\.\d+\.\d+)""\s*\}",
        RegexOptions.Compiled);

    public static string? Read(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return null;
        return _cache.GetOrAdd(folderPath, ReadUncached);
    }

    private static string? ReadUncached(string folderPath)
    {
        try
        {
            string spa = Path.Combine(folderPath, "transporter", "spa");
            if (!Directory.Exists(spa)) return null;
            string? main = Directory.EnumerateFiles(spa, "main-es2015.*.js").FirstOrDefault()
                        ?? Directory.EnumerateFiles(spa, "main-es5.*.js").FirstOrDefault()
                        ?? Directory.EnumerateFiles(spa, "main*.js").FirstOrDefault();
            if (main is null) return null;

            string text = File.ReadAllText(main);
            string? fallback = null;
            foreach (Match m in Rx.Matches(text))
            {
                string key = m.Groups[1].Value, ver = m.Groups[2].Value;
                if (!key.Equals("version", StringComparison.OrdinalIgnoreCase))
                    return ver;              // минифицированный ключ → это версия УТМ
                fallback ??= ver;
            }
            return fallback;
        }
        catch { return null; }
    }
}
