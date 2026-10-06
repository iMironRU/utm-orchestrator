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

    /// <summary>Отправить. Возвращает null при успехе или текст ошибки.</summary>
    Task<string?> SendAsync(AlertMessage msg, CancellationToken ct = default);
}
