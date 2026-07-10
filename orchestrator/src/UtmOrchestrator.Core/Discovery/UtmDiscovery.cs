using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UtmOrchestrator.Core.Diagnostics;
using UtmOrchestrator.Core.State;
using UtmOrchestrator.Core.Tokens;

namespace UtmOrchestrator.Core.Discovery;

/// <summary>
/// «Attach existing»: находит уже установленные УТМ (службы Transport*) на машине,
/// вычисляет их папку/порт (из реестра procrun + transport.properties) и, если
/// служба отвечает, текущий ownerId (ФСРАР). Сопоставляет ФСРАР с серийником
/// подключённого токена. Полностью read-only.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UtmDiscovery
{
    private static readonly Regex ServicePattern = new(@"^Transport\d*$", RegexOptions.Compiled);

    public static async Task<List<UtmInstance>> DiscoverAsync(CancellationToken ct = default)
    {
        var result = new List<UtmInstance>();

        // ФСРАР -> серийник по реально подключённым токенам
        var fsrarToSerial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var t in new TokenScanner().Scan())
                if (t.HasFsrar) fsrarToSerial[t.FsrarId!] = t.Serial;
        }
        catch { /* токены недоступны — не критично для обнаружения служб */ }

        using var http = new UtmHttpClient();

        foreach (var svc in ServiceController.GetServices())
        {
            using (svc)
            {
                if (!ServicePattern.IsMatch(svc.ServiceName)) continue;

                string? transporterDir = GetTransporterDir(svc.ServiceName);
                int port = transporterDir != null ? ReadWebPort(transporterDir) ?? 0 : 0;

                var inst = new UtmInstance
                {
                    ServiceName = svc.ServiceName,
                    Port = port,
                    FolderPath = transporterDir != null
                        ? Directory.GetParent(transporterDir)?.FullName ?? string.Empty
                        : string.Empty,
                };

                if (port > 0)
                {
                    var info = await http.GetInfoAsync(port, ct).ConfigureAwait(false);
                    inst.ExpectedFsrar = info?.OwnerId;
                    if (inst.ExpectedFsrar != null && fsrarToSerial.TryGetValue(inst.ExpectedFsrar, out var serial))
                        inst.TokenSerial = serial;
                }

                result.Add(inst);
            }
        }

        result.Sort((a, b) => a.Port.CompareTo(b.Port));
        return result;
    }

    /// <summary>Каталог ...\transporter службы — из procrun-реестра (-Dbasedir=...).</summary>
    private static string? GetTransporterDir(string service)
    {
        // procrun (prunsrv/utm.exe) — 32-битный, конфиг под WOW6432Node
        foreach (var root in new[]
                 {
                     $@"SOFTWARE\WOW6432Node\Apache Software Foundation\Procrun 2.0\{service}\Parameters\Java",
                     $@"SOFTWARE\Apache Software Foundation\Procrun 2.0\{service}\Parameters\Java",
                 })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key?.GetValue("Options") is string[] opts)
            {
                foreach (var o in opts)
                {
                    const string prefix = "-Dbasedir=";
                    if (o.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return o.Substring(prefix.Length);
                }
            }
        }
        return null;
    }

    private static int? ReadWebPort(string transporterDir)
    {
        string path = Path.Combine(transporterDir, "conf", "transport.properties");
        if (!File.Exists(path)) return null;
        foreach (var line in File.ReadLines(path))
        {
            string t = line.Trim();
            const string prefix = "web.server.port=";
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(t.Substring(prefix.Length).Trim(), out int p))
                return p;
        }
        return null;
    }
}
