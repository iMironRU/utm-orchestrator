namespace UtmOrchestrator.Service;

/// <summary>
/// Общий флаг «сейчас идёт подъём/перепривязка УТМ». Пока он активен, панель и трей
/// показывают «Запускается…» вместо «Сбой»: во время подъёма УТМ поднимаются по
/// одному несколько минут, и промежуточные «не отвечает» — это норма, а не поломка.
/// Счётчик (а не bool), чтобы одновременные boot-подъём и /api/utm/restart не гасили
/// флаг друг у друга. Использовать: <c>using (BringUpStatus.Begin()) { ... }</c>.
/// </summary>
public static class BringUpStatus
{
    private static int _active;

    public static bool Active => Volatile.Read(ref _active) > 0;

    public static IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                Interlocked.Decrement(ref _active);
        }
    }
}
