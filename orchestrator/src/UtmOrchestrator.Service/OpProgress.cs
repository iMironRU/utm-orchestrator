namespace UtmOrchestrator.Service;

/// <summary>
/// Живой прогресс длинных операций (установка на все токены, привязка) — чтобы веб
/// показывал «ставлю 3 из 8: … — регистрирую», а не немое «ждите». Операция крутится
/// в фоне (Task.Run), эндпоинт сразу отвечает Accepted, а клиент опрашивает /api/status
/// и видит прогресс. Импорт бандлов ведёт свой прогресс на клиенте (там цикл по файлам),
/// но панель одна и та же. Потокобезопасно, живёт время процесса.
/// </summary>
public static class OpProgress
{
    private static readonly object _lock = new();
    private static bool _active;
    private static string _title = "";
    private static int _total;
    private static int _done;
    private static string _phase = "";
    private static string? _current;

    public sealed record Snapshot(bool Active, string Title, int Total, int Done, string Phase, string? Current);

    /// <summary>Начать операцию: заголовок и общее число шагов.</summary>
    public static void Start(string title, int total)
    {
        lock (_lock) { _active = true; _title = title; _total = total; _done = 0; _phase = "подготовка…"; _current = null; }
    }

    /// <summary>Обновить: сколько сделано, текущая фаза и над каким элементом работаем.</summary>
    public static void Update(int done, string phase, string? current = null)
    {
        lock (_lock) { _done = done; _phase = phase; _current = current; }
    }

    public static void Finish()
    {
        lock (_lock) { _active = false; _phase = "готово"; _current = null; }
    }

    public static Snapshot Get()
    {
        lock (_lock) { return new Snapshot(_active, _title, _total, _done, _phase, _current); }
    }
}
