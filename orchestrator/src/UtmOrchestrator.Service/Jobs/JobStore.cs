using System.Collections.Concurrent;

namespace UtmOrchestrator.Service.Jobs;

public enum JobState { Pending, Running, Done, Error }

/// <summary>
/// Интерактивное задание, которое служба (session 0) сама выполнить не может, и
/// которое подхватывает трей в пользовательской сессии: скан токенов (PKCS11) для
/// установки/обследования, лечение токенов (рестарт SCardSvr) и т.п.
/// </summary>
public sealed class Job
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Type { get; init; } = "";      // "scan" | "heal" | ...
    public string? Params { get; init; }          // JSON-параметры (опционально)
    public JobState State { get; set; } = JobState.Pending;
    public string? Result { get; set; }           // JSON-результат
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
}

/// <summary>
/// Очередь интерактивных заданий (в памяти). Веб кладёт задание, трей забирает
/// «pending», выполняет и возвращает результат, веб опрашивает по id.
/// Задания живут время работы процесса — для разовых интерактивных операций этого
/// достаточно (при рестарте службы незавершённое задание просто теряется).
/// </summary>
public sealed class JobStore
{
    private readonly ConcurrentDictionary<string, Job> _all = new();
    private readonly ConcurrentQueue<string> _pending = new();

    public Job Create(string type, string? prms)
    {
        var job = new Job { Type = type, Params = prms };
        _all[job.Id] = job;
        _pending.Enqueue(job.Id);
        Prune();
        return job;
    }

    /// <summary>Забрать следующее pending-задание (трей). Помечает Running.</summary>
    public Job? TakePending()
    {
        while (_pending.TryDequeue(out var id))
        {
            if (_all.TryGetValue(id, out var job) && job.State == JobState.Pending)
            {
                job.State = JobState.Running;
                return job;
            }
        }
        return null;
    }

    public Job? Get(string id) => _all.TryGetValue(id, out var j) ? j : null;

    public void Complete(string id, string? result, string? error)
    {
        if (!_all.TryGetValue(id, out var job)) return;
        job.Result = result;
        job.Error = error;
        job.State = string.IsNullOrEmpty(error) ? JobState.Done : JobState.Error;
    }

    // Не копим бесконечно: держим последние ~50 и убираем старьё (>10 мин).
    private void Prune()
    {
        if (_all.Count <= 50) return;
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        foreach (var kv in _all)
            if (kv.Value.State is JobState.Done or JobState.Error && kv.Value.CreatedAtUtc < cutoff)
                _all.TryRemove(kv.Key, out _);
    }
}
