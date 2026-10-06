using UtmOrchestrator.Core.Health;

namespace UtmOrchestrator.Core.Alerts;

/// <summary>Сборка каналов из настроек и рассылка уведомления во все включённые.</summary>
public static class AlertNotifier
{
    /// <summary>Вердикт здоровья → класс уведомления. Ok/Stopped/Unknown/переходные — не уведомляем (null).</summary>
    public static AlertKind? KindForVerdict(HealthVerdict v) => v switch
    {
        HealthVerdict.Faulty => AlertKind.Faulty,
        HealthVerdict.NeedRsa => AlertKind.NeedRsa,
        HealthVerdict.SigningBroken => AlertKind.SigningBroken,
        HealthVerdict.SigningUnconfirmed => AlertKind.SigningUnconfirmed,
        _ => null,
    };

    public static IReadOnlyList<IAlertChannel> BuildChannels(AlertSettings s) => new IAlertChannel[]
    {
        new EmailAlertChannel(s.Email),
        new TelegramAlertChannel(s.Telegram),
        new MaxAlertChannel(s.Max),
    };

    /// <summary>Отправить во все ВКЛЮЧЁННЫЕ каналы. Возвращает карту канал→ошибка(null=успех).</summary>
    public static async Task<Dictionary<string, string?>> SendAsync(AlertSettings s, AlertMessage msg, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string?>();
        foreach (var ch in BuildChannels(s))
        {
            if (!ch.Enabled) continue;
            try { result[ch.Name] = await ch.SendAsync(msg, ct).ConfigureAwait(false); }
            catch (Exception e) { result[ch.Name] = $"{ch.Name}: {e.Message}"; }
        }
        return result;
    }
}
