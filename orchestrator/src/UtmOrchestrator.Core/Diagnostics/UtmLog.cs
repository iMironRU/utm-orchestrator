using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UtmOrchestrator.Core.Diagnostics;

/// <summary>
/// Чтение собственного лога УТМ (<c>&lt;папка&gt;\transporter\l\transport_info.log</c>):
/// хвост строк для просмотра и РЕАЛЬНЫЙ статус обмена с ЕГАИС (по факту, а не по
/// «жив/сертификаты ок»). Обмен УТМ ведёт циклами по расписанию; признак живого
/// обмена — свежая строка «Завершение задачи обмена документами».
/// </summary>
public static class UtmLog
{
    public sealed record Line(string Time, string Level, string Text);

    public sealed record Exchange(
        DateTime? LastLocal, int? SecondsAgo, bool Live,
        int? PendingCheques, int? PendingQueries, int? PendingAscp, string? LastError);

    // 2026-07-13 13:35:14,979 INFO  ru.centerinform... - текст
    private static readonly Regex LineRx = new(
        @"^(?<t>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}),\d+\s+(?<lvl>[A-Z]+)\s+\S+\s+-\s+(?<msg>.*)$",
        RegexOptions.Compiled);

    // Живым обмен считаем, если последний цикл завершился не дольше этого назад.
    private static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(6);

    private const string DoneMark = "Завершение задачи обмена документами";
    private const string TsFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Путь к transport_info.log по корневой папке УТМ (например C:\UTM).</summary>
    public static string? LogPath(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        string p = Path.Combine(folder!, "transporter", "l", "transport_info.log");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Хвост лога: последние строки (опц. фильтр по уровню INFO/WARN/ERROR).</summary>
    public static List<Line> Tail(string? folder, int maxLines = 300, string? level = null)
    {
        var result = new List<Line>();
        string? path = LogPath(folder);
        if (path is null) return result;

        foreach (var raw in ReadTailRaw(path, 512 * 1024))
        {
            var m = LineRx.Match(raw);
            if (!m.Success) continue;
            string lvl = m.Groups["lvl"].Value;
            if (!string.IsNullOrEmpty(level) && !string.Equals(lvl, level, StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(new Line(m.Groups["t"].Value, lvl, m.Groups["msg"].Value.Trim()));
        }
        if (result.Count > maxLines) result = result.GetRange(result.Count - maxLines, maxLines);
        return result;
    }

    /// <summary>Реальный статус обмена: время последнего завершённого цикла + счётчики.</summary>
    public static Exchange ReadExchange(string? folder)
    {
        string? path = LogPath(folder);
        if (path is null) return new Exchange(null, null, false, null, null, null, null);

        DateTime? last = null;
        int? cheques = null, queries = null, ascp = null;
        string? lastError = null;

        // Читаем последний блок и идём с конца — свежие записи важнее.
        var lines = ReadTailRaw(path, 512 * 1024);
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var m = LineRx.Match(lines[i]);
            if (!m.Success) continue;
            string msg = m.Groups["msg"].Value;

            if (last is null && msg.Contains(DoneMark, StringComparison.Ordinal)
                && DateTime.TryParseExact(m.Groups["t"].Value, TsFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                last = t;

            if (cheques is null && TryCounter(msg, "чеков", out int c)) cheques = c;
            if (queries is null && TryCounter(msg, "запросов", out int q)) queries = q;
            if (ascp is null && TryCounter(msg, "АСКП", out int a)) ascp = a;

            if (lastError is null && string.Equals(m.Groups["lvl"].Value, "ERROR", StringComparison.Ordinal))
                lastError = msg.Length > 200 ? msg[..200] : msg;

            if (last is not null && cheques is not null && queries is not null && ascp is not null) break;
        }

        int? ago = last is DateTime dt ? (int)Math.Max(0, (DateTime.Now - dt).TotalSeconds) : (int?)null;
        bool live = last is DateTime d && (DateTime.Now - d) <= LiveWindow;
        return new Exchange(last, ago, live, cheques, queries, ascp, lastError);
    }

    // «Счетчик непогашенных чеков: [0]» → 0
    private static bool TryCounter(string msg, string kind, out int value)
    {
        value = 0;
        int k = msg.IndexOf(kind, StringComparison.Ordinal);
        if (k < 0 || !msg.Contains("непогашен", StringComparison.Ordinal)) return false;
        var m = Regex.Match(msg, @"\[(\d+)\]");
        return m.Success && int.TryParse(m.Groups[1].Value, out value);
    }

    // Последние ~maxBytes файла как строки (файл активно пишется — ReadWrite share).
    private static List<string> ReadTailRaw(string path, int maxBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string content = sr.ReadToEnd();
            var lines = content.Replace("\r", "").Split('\n');
            // Если читали не с начала файла — первая строка может быть обрезана.
            var list = new List<string>(lines);
            if (start > 0 && list.Count > 0) list.RemoveAt(0);
            return list;
        }
        catch { return new List<string>(); }
    }
}
