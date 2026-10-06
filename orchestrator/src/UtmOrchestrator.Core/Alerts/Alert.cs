namespace UtmOrchestrator.Core.Alerts;

/// <summary>Класс события для уведомления — по нему решаем, слать ли (настройка событий).</summary>
public enum AlertKind
{
    Faulty,            // сбой (служба/HTTP/не тот токен и т.п.)
    NeedRsa,           // нужен перевыпуск RSA (в т.ч. рассинхрон RSA↔ГОСТ)
    SigningBroken,     // подпись падает (CKR/крипто-DLL)
    SigningUnconfirmed,// подпись не подтверждена
    Recovery,          // УТМ вернулся в норму
    Test,              // тестовое сообщение из настроек
}

/// <summary>Готовое к отправке уведомление (канал-независимое). Title/Body — для текстовых каналов
/// (Telegram/MAX, plain-text часть письма); структурные поля — для HTML-письма (карточка).</summary>
public sealed record AlertMessage(
    AlertKind Kind,
    string Title,   // короткая строка темы: «УТМ «Донковцева»: нужен перевыпуск RSA»
    string Body)    // тело: причина + детали + рекомендация
{
    /// <summary>Имя машины-оркестратора.</summary>
    public string? Machine { get; init; }
    /// <summary>Человеческое имя УТМ («Донковцева») или имя службы.</summary>
    public string? Utm { get; init; }
    /// <summary>Имя службы Windows (Transport, UTM_2…).</summary>
    public string? Service { get; init; }
    public string? Fsrar { get; init; }
    public int? Port { get; init; }
    /// <summary>Причина из HealthChecker (как в карточке панели).</summary>
    public string? Reason { get; init; }
    /// <summary>Что делать — короткая инструкция по типу события.</summary>
    public string? Hint { get; init; }
    /// <summary>Ссылка на панель оркестратора (http://ip:8090).</summary>
    public string? PanelUrl { get; init; }
    /// <summary>Ссылка на веб-интерфейс самого УТМ (http://ip:port).</summary>
    public string? UtmUrl { get; init; }
    public DateTime When { get; init; } = DateTime.Now;
    /// <summary>Доп. строки «ключ → значение» (напр. включённые события в тесте).</summary>
    public IReadOnlyList<(string Key, string Value)>? Extra { get; init; }
}

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
