using System.Text;
using System.Text.RegularExpressions;

namespace UtmOrchestrator.Core.Diagnostics;

/// <summary>
/// Самолечение «отравленного» transport.properties. Сканеры шлют УТМ заголовок X-Forwarded-For с
/// Log4Shell-пробой («${jndi:ldap://…${hostName}…}»), УТМ сохраняет его в список web.server.attempt.ip,
/// а на старте Spring принимает «${…}» за подстановку, не может её разрешить и роняет Tomcat — служба
/// стартует и сразу останавливается. Вычищаем из IP-списков всё, что не похоже на адрес.
/// </summary>
public static class UtmConfigSanitizer
{
    private static readonly Regex IpKey = new(@"^\s*web\.server\.(attempt|access)\.ip\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // IPv4/IPv6/CIDR: только цифры, hex, точки, двоеточия, слэш
    private static readonly Regex IpItem = new(@"^[0-9a-fA-F:.\/]+$", RegexOptions.Compiled);

    /// <summary>Результат: сколько элементов удалено и какие (для лога/уведомления).</summary>
    public sealed record Result(int Removed, IReadOnlyList<string> Items, string Path);

    /// <summary>Лечит конфиг по папке УТМ (…\transporter\conf\transport.properties). null — менять нечего.</summary>
    public static Result? SanitizeFolder(string? utmFolder)
    {
        if (string.IsNullOrWhiteSpace(utmFolder)) return null;
        return SanitizeFile(Path.Combine(utmFolder, "transporter", "conf", "transport.properties"));
    }

    /// <summary>Лечит конфиг по имени службы (папка берётся из ImagePath службы). null — менять нечего/не найдено.</summary>
    public static Result? SanitizeService(string serviceName)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            string? img = k?.GetValue("ImagePath") as string;
            if (string.IsNullOrWhiteSpace(img)) return null;
            // "C:\...\utm-2\transporter\bin\utm.exe" //RS//Name  → папка = на 3 уровня выше exe
            string exe = img.TrimStart().StartsWith('"') ? img.Split('"')[1] : img.Split(' ')[0];
            string? bin = Path.GetDirectoryName(exe);
            string? folder = bin is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(bin));
            return SanitizeFolder(folder);
        }
        catch { return null; }
    }

    public static Result? SanitizeFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (!text.Contains("${", StringComparison.Ordinal)) return null;   // быстрый выход: здоровый конфиг

            bool crlf = text.Contains("\r\n");
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var removed = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (!IpKey.IsMatch(lines[i])) continue;
                int eq = lines[i].IndexOf('=');
                var keep = new List<string>();
                foreach (var raw in lines[i][(eq + 1)..].Split(','))
                {
                    string item = raw.Trim();
                    if (item.Length == 0) continue;
                    if (IpItem.IsMatch(item)) keep.Add(item);
                    else removed.Add(item.Length > 120 ? item[..120] + "…" : item);
                }
                if (removed.Count > 0) lines[i] = lines[i][..(eq + 1)] + " " + string.Join(",", keep);
            }
            if (removed.Count == 0) return null;

            File.Copy(path, path + ".bak_poison", overwrite: true);
            File.WriteAllText(path, string.Join(crlf ? "\r\n" : "\n", lines), new UTF8Encoding(false));
            return new Result(removed.Count, removed, path);
        }
        catch { return null; }
    }
}
