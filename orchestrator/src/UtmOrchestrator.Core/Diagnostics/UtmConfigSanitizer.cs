using System.Text;
using System.Text.RegularExpressions;

namespace UtmOrchestrator.Core.Diagnostics;

/// <summary>
/// Самолечение «отравленного» transport.properties. Сканеры шлют УТМ заголовок X-Forwarded-For с
/// Log4Shell-пробой («${jndi:ldap://…${hostName}…}»), УТМ сохраняет его в список web.server.attempt.ip,
/// а на старте Spring принимает «${…}» за подстановку, не может её разрешить и роняет Tomcat — служба
/// стартует и сразу останавливается. Если журнал web.server.attempt.ip содержит не-адреса, сбрасываем его, оставляя только разрешённые (web.server.access.ip).
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
            bool crlf = text.Contains("\r\n");
            var lines = text.Replace("\r\n", "\n").Split('\n');

            int attemptIdx = -1;
            var access = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                var m = IpKey.Match(lines[i]);
                if (!m.Success) continue;
                string val = lines[i][(lines[i].IndexOf('=') + 1)..];
                if (m.Groups[1].Value.Equals("attempt", StringComparison.OrdinalIgnoreCase)) attemptIdx = i;
                else access.AddRange(val.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0 && IpItem.IsMatch(s)));
            }
            if (attemptIdx < 0) return null;

            // Журнал попыток отравлен, если в нём есть что-то кроме адресов (в т.ч. «${jndi:…}»).
            int eq = lines[attemptIdx].IndexOf('=');
            var items = lines[attemptIdx][(eq + 1)..].Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var bad = items.Where(s => !IpItem.IsMatch(s)).ToList();
            if (bad.Count == 0) return null;

            // Лечение: attempt.ip — только журнал отклонённых попыток. Оставляем в нём лишь разрешённые адреса
            // (web.server.access.ip), всё остальное сбрасываем — так же, как вручную.
            var keep = items.Where(s => access.Contains(s)).Distinct().ToList();
            lines[attemptIdx] = lines[attemptIdx][..(eq + 1)] + " " + string.Join(",", keep);

            File.Copy(path, path + ".bak_poison", overwrite: true);
            File.WriteAllText(path, string.Join(crlf ? "\r\n" : "\n", lines), new UTF8Encoding(false));
            var shown = bad.Where(s => s.Contains("${") || s.Contains("jndi", StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(bad[0])
                           .Select(s => s.Length > 120 ? s[..120] + "…" : s).Take(5).ToList();
            return new Result(bad.Count, shown, path);
        }
        catch { return null; }
    }
}
