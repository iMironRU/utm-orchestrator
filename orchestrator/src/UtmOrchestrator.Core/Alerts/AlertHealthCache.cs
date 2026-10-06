using System.Collections.Concurrent;

namespace UtmOrchestrator.Core.Alerts;

/// <summary>
/// Последний результат проверки доступности по каждому каналу (telegram/max/email). Заполняется
/// фоновой проверкой (AlertWorker, ~раз в 10 мин) и кнопкой «Проверить доступность» в UI.
/// Нужен, чтобы ЗАРАНЕЕ видеть, дойдёт ли уведомление (напр. api.telegram.org недоступен через
/// прокси), а не узнавать об этом, когда уведомление уже не ушло.
/// </summary>
public static class AlertHealthCache
{
    private static readonly ConcurrentDictionary<string, ChannelHealth> _c = new(StringComparer.OrdinalIgnoreCase);

    public static void Set(ChannelHealth h) => _c[h.Channel] = h;

    public static IReadOnlyCollection<ChannelHealth> All() => _c.Values.ToArray();

    public static ChannelHealth? Get(string channel) => _c.TryGetValue(channel, out var h) ? h : null;

    /// <summary>Есть ли хоть один ВКЛЮЧЁННЫЙ канал, доступный по последней проверке.</summary>
    public static bool AnyEnabledReachable(AlertSettings s) =>
        AlertNotifier.BuildChannels(s).Any(ch => ch.Enabled && Get(ch.Name)?.Ok == true);
}
