namespace UtmOrchestrator.Core.Alerts;

/// <summary>Класс события для уведомления — по нему решаем, слать ли (настройка событий).</summary>
public enum AlertKind
{
    Faulty,            // сбой (служба/HTTP/не тот токен и т.п.)
    NeedRsa,           // нужен перевыпуск RSA (в т.ч. рассинхрон RSA↔ГОСТ)
    SigningBroken,     // подпись падает (CKR/крипто-DLL)
    SigningUnconfirmed,// подпись не подтверждена
    Recovery,          // УТМ вернулся в норму
}

/// <summary>Готовое к отправке уведомление (канал-независимое).</summary>
public sealed record AlertMessage(
    AlertKind Kind,
    string Title,   // короткая строка темы: «УТМ «Донковцева»: нужен перевыпуск RSA»
    string Body);   // тело: причина + детали + рекомендация

/// <summary>Канал доставки уведомления (Email/Telegram/MAX/…). Реализации не кидают — возвращают ошибку текстом.</summary>
public interface IAlertChannel
{
    /// <summary>Имя канала для логов/теста («email», «telegram», «max»).</summary>
    string Name { get; }

    /// <summary>Включён ли канал (по настройкам).</summary>
    bool Enabled { get; }

    /// <summary>Есть ли реквизиты для проверки/отправки (хост/токен). Проверять доступность можно и у выключенного.</summary>
    bool Configured { get; }

    /// <summary>Отправить. Возвращает null при успехе или текст ошибки.</summary>
    Task<string?> SendAsync(AlertMessage msg, CancellationToken ct = default);

    /// <summary>Проверить доступность сервера канала и валидность реквизитов БЕЗ отправки сообщения
    /// (Telegram getMe, MAX /me, SMTP connect+auth). Ok=false с понятной причиной: недоступен / токен отклонён.</summary>
    Task<(bool Ok, string Detail)> CheckAsync(CancellationToken ct = default);
}

/// <summary>Результат проверки доступности канала (для UI и фонового мониторинга).</summary>
public sealed record ChannelHealth(string Channel, bool Ok, string Detail, DateTime CheckedAtUtc);
