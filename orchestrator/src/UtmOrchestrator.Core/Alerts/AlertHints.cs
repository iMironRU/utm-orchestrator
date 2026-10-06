namespace UtmOrchestrator.Core.Alerts;

/// <summary>Подписи, цвета и короткие инструкции «что делать» по типу события — те же шаги, что в карточках панели.</summary>
public static class AlertHints
{
    public static string Label(AlertKind k) => k switch
    {
        AlertKind.Faulty => "Сбой УТМ",
        AlertKind.NeedRsa => "Нужен перевыпуск RSA",
        AlertKind.SigningBroken => "Подпись падает",
        AlertKind.SigningUnconfirmed => "Подпись не подтверждена",
        AlertKind.Recovery => "УТМ вернулся в норму",
        AlertKind.Test => "Тест уведомлений",
        _ => k.ToString(),
    };

    /// <summary>Цвет шапки письма (HEX) по типу события.</summary>
    public static string Color(AlertKind k) => k switch
    {
        AlertKind.Faulty => "#c62828",
        AlertKind.SigningBroken => "#c62828",
        AlertKind.NeedRsa => "#ef6c00",
        AlertKind.SigningUnconfirmed => "#f9a825",
        AlertKind.Recovery => "#2e7d32",
        AlertKind.Test => "#1565c0",
        _ => "#455a64",
    };

    public static string Emoji(AlertKind k) => k switch
    {
        AlertKind.Faulty => "🔴",
        AlertKind.SigningBroken => "🔴",
        AlertKind.NeedRsa => "🟠",
        AlertKind.SigningUnconfirmed => "🟡",
        AlertKind.Recovery => "🟢",
        AlertKind.Test => "🔵",
        _ => "⚪",
    };

    public static string? Hint(AlertKind k) => k switch
    {
        AlertKind.Faulty =>
            "Откройте панель оркестратора — в карточке УТМ описана причина и предложены действия " +
            "(«Поднять все», «Привязать все токены», перезапуск). Если УТМ не отвечает по HTTP — проверьте, " +
            "что токен на месте и служба запущена.",
        AlertKind.NeedRsa =>
            "Перевыпустите RSA в веб-интерфейсе самого УТМ (раздел «Сертификаты») — это штатная операция, " +
            "КЭП перевыпускать НЕ нужно. Делайте на стабильном парке: все токены на месте, другие операции " +
            "не идут. Если УТМ отвечает ошибкой CKR_SESSION_HANDLE_INVALID — сначала перезапустите его.",
        AlertKind.SigningBroken =>
            "Подпись ГОСТ падает в самом УТМ. Лесенка из карточки панели: перезапуск УТМ → «Изолировать ГОСТ» → " +
            "переткнуть токен → проверить, не нужен ли перевыпуск RSA. Учётная система получает 500 на /opt/in — " +
            "документы не уходят, пока не починим.",
        AlertKind.SigningUnconfirmed =>
            "В логе были ошибки подписи, но свежих запросов нет — не ясно, починилось ли. Отправьте любой документ " +
            "из учётной системы (или QueryRests) и посмотрите статус в панели.",
        AlertKind.Recovery =>
            "Подпись и обмен восстановлены, действий не требуется. Если в учётной системе остались «зависшие» " +
            "документы — отправьте их повторно.",
        AlertKind.Test =>
            "Если вы видите это письмо — канал настроен верно. Так будут выглядеть уведомления о сбоях.",
        _ => null,
    };
}
