using System.Runtime.Versioning;

namespace UtmOrchestrator.Core.Firewall;

/// <summary>
/// Проверяет, «открыт» ли TCP-порт УТМ во входящих правилах брандмауэра Windows.
/// Читает правила через COM (HNetCfg.FwPolicy2) — не зависит от локали (в отличие от
/// парсинга вывода netsh на русской ОС).
///
/// «Открыт» = есть ВКЛючённое входящее разрешающее правило, у которого LocalPorts
/// содержит ИМЕННО этот номер порта (число или диапазон). Правила с LocalPorts=Any/*
/// НЕ учитываем: почти всегда они привязаны к программе/службе (RemoteAssistance,
/// CDPSvc и т.п.) и не означают, что открыт конкретно порт УТМ. Это совпадает с
/// логикой оператора: «порт перечислен в правиле» (как общее правило «UTM» 8080-8085
/// или наши правила на каждый УТМ).
///
/// Результат кэшируется на короткое время: /api/status опрашивается часто, а полный
/// обход правил недёшев; состояние брандмауэра меняется редко. После наших правок
/// (открыть/закрыть порт) кэш сбрасывается через <see cref="Invalidate"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FirewallInspector
{
    private const int DirIn = 1;      // NET_FW_RULE_DIRECTION_IN
    private const int ActionAllow = 1; // NET_FW_ACTION_ALLOW
    private const int ProtoTcp = 6;
    private const int ProtoAny = 256;

    private static readonly object _lock = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);
    private static List<(int Lo, int Hi)>? _ranges;
    private static DateTime _atUtc;

    /// <summary>true, если порт покрыт хотя бы одним подходящим числовым правилом.</summary>
    public static bool IsOpen(int port)
    {
        if (port <= 0) return false;
        foreach (var (lo, hi) in GetRanges())
            if (port >= lo && port <= hi) return true;
        return false;
    }

    /// <summary>Сбросить кэш (после изменения правил через FirewallManager).</summary>
    public static void Invalidate()
    {
        lock (_lock) { _ranges = null; }
    }

    private static List<(int, int)> GetRanges()
    {
        lock (_lock)
        {
            if (_ranges is not null && DateTime.UtcNow - _atUtc < Ttl)
                return _ranges;

            var list = new List<(int, int)>();
            try
            {
                var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t is not null)
                {
                    dynamic policy = Activator.CreateInstance(t)!;
                    foreach (dynamic rule in policy.Rules)
                    {
                        try
                        {
                            if (!(bool)rule.Enabled) continue;
                            if ((int)rule.Direction != DirIn) continue;
                            if ((int)rule.Action != ActionAllow) continue;
                            int proto = (int)rule.Protocol;
                            if (proto != ProtoTcp && proto != ProtoAny) continue;
                            string? lp = rule.LocalPorts as string;
                            ParsePorts(lp, list);
                        }
                        catch { /* отдельное правило нечитаемо — пропускаем */ }
                    }
                }
            }
            catch { /* COM недоступен — считаем, что ничего не открыто */ }

            _ranges = list;
            _atUtc = DateTime.UtcNow;
            return list;
        }
    }

    // Разбирает LocalPorts ("8080", "8080-8085", "8080,8081,9000-9005") в числовые
    // диапазоны. Спец-значения (Any, *, RPC, RPC-EPMap, Teredo, …) игнорируем.
    private static void ParsePorts(string? localPorts, List<(int, int)> into)
    {
        if (string.IsNullOrWhiteSpace(localPorts)) return;
        foreach (var tokenRaw in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = tokenRaw.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(tokenRaw[..dash], out int lo) &&
                    int.TryParse(tokenRaw[(dash + 1)..], out int hi) && lo <= hi)
                    into.Add((lo, hi));
            }
            else if (int.TryParse(tokenRaw, out int p))
            {
                into.Add((p, p));
            }
            // не-число (Any/*/RPC/…) — пропускаем
        }
    }
}
