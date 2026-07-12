using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace UtmOrchestrator.Core.Network;

/// <summary>
/// Управление пробросами портов на роутере через UPnP IGD (COM-объект
/// <c>HNetCfg.NATUPnP</c>, он же Windows Internet Gateway Device Discovery/Control).
/// Роутер должен иметь ВКЛЮЧЁННЫЙ UPnP — иначе StaticPortMappingCollection == null.
/// Все вызовы COM обёрнуты таймаутом: без ответа роутера геттер может залипать.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UpnpManager
{
    public sealed record Mapping(string ExternalIp, int ExternalPort, string Protocol,
        int InternalPort, string InternalClient, bool Enabled, string Description);

    public sealed record NetStatus(
        bool Manageable, string? ExternalIp, string? LanIp, IReadOnlyList<Mapping> Mappings, string? Error);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private static NetStatus? _cached;
    private static DateTime _cachedAtUtc;
    private static readonly object _lock = new();

    /// <summary>Результат последнего опроса (null — ещё не опрашивали). true = роутером управляем.</summary>
    public static bool? LastManageable => _cached?.Manageable;

    /// <summary>Опрос с кэшем: не чаще, чем раз в maxAge (COM-опрос медленный).</summary>
    public static NetStatus CachedProbe(TimeSpan maxAge)
    {
        lock (_lock)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAtUtc < maxAge) return _cached;
            _cached = Probe();
            _cachedAtUtc = DateTime.UtcNow;
            return _cached;
        }
    }

    /// <summary>Полный опрос сети: управляем ли роутером, внешний IP, наш LAN-IP, список пробросов.</summary>
    public static NetStatus Probe()
    {
        string? lan = LanIp();
        try
        {
            return WithTimeout(() =>
            {
                dynamic? nat = CreateNat();
                if (nat is null) return new NetStatus(false, null, lan, Array.Empty<Mapping>(), "нет HNetCfg.NATUPnP");
                dynamic? col = nat.StaticPortMappingCollection;
                if (col is null)
                    return new NetStatus(false, null, lan, Array.Empty<Mapping>(), "роутер не отдаёт UPnP (выключен?)");

                var list = new List<Mapping>();
                foreach (dynamic m in col)
                {
                    list.Add(new Mapping(
                        Str(m.ExternalIPAddress), (int)m.ExternalPort, Str(m.Protocol),
                        (int)m.InternalPort, Str(m.InternalClient), (bool)m.Enabled, Str(m.Description)));
                }
                string? extIp = list.Select(x => x.ExternalIp).FirstOrDefault(ip => !string.IsNullOrWhiteSpace(ip));
                return new NetStatus(true, extIp, lan, list, null);
            }) ?? new NetStatus(false, null, lan, Array.Empty<Mapping>(), "таймаут опроса роутера");
        }
        catch (Exception e)
        {
            return new NetStatus(false, null, lan, Array.Empty<Mapping>(), e.Message);
        }
    }

    /// <summary>Создать/обновить проброс WAN:externalPort → lanIp:internalPort (TCP).</summary>
    public static bool AddMapping(int externalPort, int internalPort, string lanIp, string description, Action<string>? log = null)
    {
        try
        {
            bool ok = WithTimeout(() =>
            {
                dynamic? nat = CreateNat();
                dynamic? col = nat?.StaticPortMappingCollection;
                if (col is null) { log?.Invoke("UPnP недоступен"); return false; }
                col.Add(externalPort, "TCP", internalPort, lanIp, true, description);
                return true;
            });
            log?.Invoke($"UPnP add {externalPort}->{lanIp}:{internalPort} = {ok}");
            return ok;
        }
        catch (Exception e) { log?.Invoke("UPnP add: " + e.Message); return false; }
    }

    /// <summary>Убрать проброс WAN:externalPort (TCP).</summary>
    public static bool RemoveMapping(int externalPort, Action<string>? log = null)
    {
        try
        {
            bool ok = WithTimeout(() =>
            {
                dynamic? nat = CreateNat();
                dynamic? col = nat?.StaticPortMappingCollection;
                if (col is null) return false;
                col.Remove(externalPort, "TCP");
                return true;
            });
            log?.Invoke($"UPnP remove {externalPort} = {ok}");
            return ok;
        }
        catch (Exception e) { log?.Invoke("UPnP remove: " + e.Message); return false; }
    }

    private static dynamic? CreateNat()
    {
        Type? t = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
        return t is null ? null : Activator.CreateInstance(t);
    }

    /// <summary>Основной IPv4 LAN-адрес этой машины (для проброса на роутере).</summary>
    public static string? LanIp()
    {
        try
        {
            // Трюк: «подключиться» UDP-сокетом наружу — ОС выберет исходящий интерфейс.
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString();
        }
    }

    /// <summary>Похож ли адрес на «серый» (RFC1918 / CGNAT 100.64/10) — тогда проброс бесполезен.</summary>
    public static bool IsPrivateOrCgnat(string? ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = a.GetAddressBytes();
        if (b[0] == 10) return true;                                   // 10.0.0.0/8
        if (b[0] == 192 && b[1] == 168) return true;                   // 192.168.0.0/16
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;      // 172.16.0.0/12
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;     // 100.64.0.0/10 CGNAT
        if (b[0] == 169 && b[1] == 254) return true;                   // link-local
        return false;
    }

    private static T? WithTimeout<T>(Func<T> work)
    {
        var task = Task.Run(work);
        return task.Wait(Timeout) ? task.Result : default;
    }

    private static string Str(object? o) => o?.ToString() ?? "";
}
