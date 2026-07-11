using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace UtmOrchestrator.Core.Migration;

/// <summary>
/// Чтение конфигурации 2UTM (`2UTM_vs`) — предшественника нашего оркестратора.
/// 2UTM крутится службой (`2UTM_vs.exe -service`) и на старте делает
/// SCardIntroduceReader по своему `config.ini`: каждому УТМ (порту) назначает свой
/// ридер (attrReader — алиас, под которым УТМ ждёт токен) от физического nameReader.
///
/// Формат config.ini (проверено на реальной машине):
///   [options] autostart=true  countUTM=6
///   [UTM_N]   nameReader=... attrReader=... serialNumber=&lt;dec&gt; port=8080
///
/// serialNumber — ДЕСЯТИЧНЫЙ; наш TokenSerial — hex того же числа
/// (1157188378 == 0x44f94b1a). attrReader соответствует нашему ReaderName.
///
/// Нужен для миграции: обнаружить 2UTM, снять его с автозапуска (не удаляя) и
/// подхватить его УТМ под наш оркестратор.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TwoUtmConfig
{
    public sealed record Utm(int Index, string NameReader, string AttrReader, string SerialHex, int Port);

    public bool Autostart { get; private set; }
    public int CountUtm { get; private set; }
    public string ConfigPath { get; private set; } = "";
    public IReadOnlyList<Utm> Utms => _utms;
    private readonly List<Utm> _utms = new();

    public static readonly string[] FolderCandidates =
    {
        @"C:\Program Files (x86)\2UTM_vs",
        @"C:\Program Files\2UTM_vs",
        @"C:\2UTM_vs",
    };

    /// <summary>Папка 2UTM (по наличию 2UTM_vs.exe), либо null.</summary>
    public static string? FindFolder()
        => FolderCandidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "2UTM_vs.exe")));

    public static string? FindConfigPath()
    {
        foreach (var d in FolderCandidates)
        {
            string p = Path.Combine(d, "config.ini");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Служба 2UTM (по PathName, содержащему 2UTM_vs), либо null (имя, состояние, режим).</summary>
    public static (string Name, string State, string StartMode)? FindService()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (key is null) return null;
        foreach (var svcName in key.GetSubKeyNames())
        {
            using var s = key.OpenSubKey(svcName);
            if (s?.GetValue("ImagePath") is string img &&
                img.Contains("2UTM_vs", StringComparison.OrdinalIgnoreCase))
            {
                int start = s.GetValue("Start") is int st ? st : -1;
                string mode = start switch { 2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => "?" };
                return (svcName, "?", mode); // состояние отдельно через ServiceControl
            }
        }
        return null;
    }

    public static TwoUtmConfig? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var cfg = new TwoUtmConfig { ConfigPath = path };
        string section = "";
        string nameReader = "", attrReader = "", serialHex = "";
        int port = 0, index = 0;

        void Flush()
        {
            if (section.StartsWith("UTM_", StringComparison.OrdinalIgnoreCase) && port > 0)
                cfg._utms.Add(new Utm(index, nameReader, attrReader, serialHex, port));
            nameReader = attrReader = serialHex = ""; port = 0;
        }

        foreach (var raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                section = line[1..^1].Trim();
                if (section.StartsWith("UTM_", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(section.AsSpan(4), out index);
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();

            if (section.Equals("options", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("autostart", StringComparison.OrdinalIgnoreCase))
                    cfg.Autostart = val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1";
                else if (key.Equals("countUTM", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int c))
                    cfg.CountUtm = c;
            }
            else if (section.StartsWith("UTM_", StringComparison.OrdinalIgnoreCase))
            {
                switch (key.ToLowerInvariant())
                {
                    case "namereader": nameReader = val; break;
                    case "attrreader": attrReader = val; break;
                    case "port": int.TryParse(val, out port); break;
                    case "serialnumber": serialHex = DecimalSerialToHex(val); break;
                }
            }
        }
        Flush();
        return cfg;
    }

    /// <summary>1157188378 → "44f94b1a" (наш формат серийника). Если не число — как есть.</summary>
    public static string DecimalSerialToHex(string dec)
        => ulong.TryParse(dec, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong n)
            ? n.ToString("x")
            : dec.Trim();
}
