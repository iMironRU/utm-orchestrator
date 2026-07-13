namespace UtmOrchestrator.Service;

/// <summary>
/// Живой прогресс boot-подъёма УТМ: сколько поднято, какая фаза, когда начали и
/// прогноз (ETA) по прошлым загрузкам. Читается быстрым /api/status БЕЗ HTTP к УТМ,
/// поэтому во время подъёма панель/трей показывают реальный прогресс, а не «висят».
/// </summary>
public static class BootProgress
{
    private static readonly object _lock = new();
    private static bool _active;
    private static int _total;
    private static int _etaSeconds;
    private static DateTime _startedUtc;
    private static string _phase = "";
    private static readonly HashSet<string> _ready = new(StringComparer.OrdinalIgnoreCase);

    public sealed record Snapshot(
        bool Active, int Total, int Ready, string Phase,
        int ElapsedSeconds, int? EtaRemainingSeconds, IReadOnlyCollection<string> ReadyServices);

    /// <summary>Начать отслеживание подъёма total УТМ с прогнозом etaSeconds (0 = неизвестно).</summary>
    public static void Start(int total, int etaSeconds)
    {
        lock (_lock)
        {
            _active = true; _total = total; _etaSeconds = etaSeconds;
            _startedUtc = DateTime.UtcNow; _phase = "подготовка…"; _ready.Clear();
        }
    }

    public static void Update(string phase, string? readyService = null)
    {
        lock (_lock)
        {
            _phase = phase;
            if (!string.IsNullOrEmpty(readyService)) _ready.Add(readyService!);
        }
    }

    public static void Finish()
    {
        lock (_lock) { _active = false; _phase = "готово"; }
    }

    public static Snapshot Get()
    {
        lock (_lock)
        {
            int elapsed = _active ? (int)Math.Max(0, (DateTime.UtcNow - _startedUtc).TotalSeconds) : 0;
            int? remain = _active && _etaSeconds > 0 ? Math.Max(0, _etaSeconds - elapsed) : (int?)null;
            return new Snapshot(_active, _total, _ready.Count, _phase, elapsed, remain, _ready.ToArray());
        }
    }
}
