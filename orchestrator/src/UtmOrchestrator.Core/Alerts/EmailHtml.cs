using System.Net;
using System.Text;

namespace UtmOrchestrator.Core.Alerts;

/// <summary>HTML-вёрстка письма уведомления: цветная шапка по типу события, карточка с полями,
/// причина, «что делать», кнопки в панель/УТМ. Таблицы + inline-стили — чтобы одинаково рендерилось
/// в Яндекс.Почте, Gmail, Outlook и мобильных клиентах.</summary>
public static class EmailHtml
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string Render(AlertMessage m)
    {
        string color = AlertHints.Color(m.Kind);
        string label = AlertHints.Label(m.Kind);
        var sb = new StringBuilder(4096);

        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">")
          .Append("<title>").Append(E(m.Title)).Append("</title></head>")
          .Append("<body style=\"margin:0;padding:0;background:#f1f3f5;font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;color:#212529;\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f1f3f5;padding:24px 12px;\"><tr><td align=\"center\">")
          .Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;width:100%;background:#ffffff;border-radius:10px;overflow:hidden;box-shadow:0 1px 4px rgba(0,0,0,.08);\">");

        // Шапка
        sb.Append("<tr><td style=\"background:").Append(color).Append(";padding:20px 24px;color:#fff;\">")
          .Append("<div style=\"font-size:12px;letter-spacing:.08em;text-transform:uppercase;opacity:.85;\">")
          .Append(E(AppInfo.ProductName)).Append(m.Machine is null ? "" : " · " + E(m.Machine)).Append("</div>")
          .Append("<div style=\"font-size:22px;font-weight:600;margin-top:6px;line-height:1.25;\">")
          .Append(AlertHints.Emoji(m.Kind)).Append(' ').Append(E(label)).Append("</div>");
        if (!string.IsNullOrWhiteSpace(m.Utm))
            sb.Append("<div style=\"font-size:16px;margin-top:4px;opacity:.95;\">УТМ «").Append(E(m.Utm)).Append("»</div>");
        sb.Append("</td></tr>");

        // Тело
        sb.Append("<tr><td style=\"padding:20px 24px 8px 24px;\">");

        if (!string.IsNullOrWhiteSpace(m.Reason))
        {
            sb.Append("<div style=\"font-size:12px;color:#6c757d;text-transform:uppercase;letter-spacing:.06em;margin-bottom:6px;\">Что случилось</div>")
              .Append("<div style=\"font-size:15px;line-height:1.5;padding:12px 14px;background:#f8f9fa;border-left:4px solid ").Append(color)
              .Append(";border-radius:4px;\">").Append(E(m.Reason).Replace("\n", "<br>")).Append("</div>");
        }
        else if (!string.IsNullOrWhiteSpace(m.Body))
        {
            sb.Append("<div style=\"font-size:15px;line-height:1.5;\">").Append(E(FirstLine(m.Body))).Append("</div>");
        }

        // Карточка полей
        var rows = new List<(string Key, string Value)>();
        if (!string.IsNullOrWhiteSpace(m.Utm)) rows.Add(("УТМ", m.Utm!));
        if (!string.IsNullOrWhiteSpace(m.Service) && !string.Equals(m.Service, m.Utm, StringComparison.Ordinal)) rows.Add(("Служба", m.Service!));
        if (!string.IsNullOrWhiteSpace(m.Fsrar)) rows.Add(("ФСРАР ID", m.Fsrar!));
        if (m.Port is int p) rows.Add(("Порт", p.ToString()));
        if (!string.IsNullOrWhiteSpace(m.Machine)) rows.Add(("Машина", m.Machine!));
        rows.Add(("Время", m.When.ToString("dd.MM.yyyy HH:mm:ss")));
        if (m.Extra is not null) rows.AddRange(m.Extra);

        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin-top:16px;border-collapse:collapse;font-size:14px;\">");
        foreach (var (k, v) in rows)
            sb.Append("<tr><td style=\"padding:8px 10px;border-bottom:1px solid #e9ecef;color:#6c757d;width:34%;vertical-align:top;\">").Append(E(k))
              .Append("</td><td style=\"padding:8px 10px;border-bottom:1px solid #e9ecef;font-weight:500;vertical-align:top;\">").Append(E(v)).Append("</td></tr>");
        sb.Append("</table>");

        // Что делать
        if (!string.IsNullOrWhiteSpace(m.Hint))
        {
            sb.Append("<div style=\"font-size:12px;color:#6c757d;text-transform:uppercase;letter-spacing:.06em;margin:20px 0 6px 0;\">Что делать</div>")
              .Append("<div style=\"font-size:14px;line-height:1.55;\">").Append(E(m.Hint)).Append("</div>");
        }

        // Кнопки
        if (!string.IsNullOrWhiteSpace(m.PanelUrl) || !string.IsNullOrWhiteSpace(m.UtmUrl))
        {
            sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:22px 0 10px 0;\"><tr>");
            if (!string.IsNullOrWhiteSpace(m.PanelUrl))
                sb.Append("<td style=\"padding-right:10px;\"><a href=\"").Append(E(m.PanelUrl)).Append("\" style=\"display:inline-block;padding:10px 18px;background:")
                  .Append(color).Append(";color:#fff;text-decoration:none;border-radius:6px;font-weight:600;font-size:14px;\">Открыть панель</a></td>");
            if (!string.IsNullOrWhiteSpace(m.UtmUrl))
                sb.Append("<td><a href=\"").Append(E(m.UtmUrl)).Append("\" style=\"display:inline-block;padding:10px 18px;background:#e9ecef;color:#212529;text-decoration:none;border-radius:6px;font-weight:600;font-size:14px;\">Веб-интерфейс УТМ</a></td>");
            sb.Append("</tr></table>");
        }
        sb.Append("</td></tr>");

        // Подвал
        sb.Append("<tr><td style=\"padding:14px 24px;background:#f8f9fa;color:#868e96;font-size:12px;line-height:1.5;border-top:1px solid #e9ecef;\">")
          .Append(E(AppInfo.ProductName)).Append(" v").Append(E(AppInfo.Version));
        if (m.Machine is not null) sb.Append(" · ").Append(E(m.Machine));
        sb.Append("<br>Письмо отправлено автоматически. Настройка событий и каналов — в панели: Настройки → Уведомления.")
          .Append("</td></tr></table></td></tr></table></body></html>");
        return sb.ToString();
    }

    /// <summary>Plain-text версия: для почтовых клиентов без HTML и для Telegram/MAX.</summary>
    public static string RenderText(AlertMessage m)
    {
        var sb = new StringBuilder();
        sb.Append(AlertHints.Emoji(m.Kind)).Append(' ').AppendLine(m.Title).AppendLine();
        if (!string.IsNullOrWhiteSpace(m.Reason)) sb.AppendLine(m.Reason).AppendLine();
        else if (!string.IsNullOrWhiteSpace(m.Body)) sb.AppendLine(FirstLine(m.Body)).AppendLine();
        if (!string.IsNullOrWhiteSpace(m.Utm)) sb.Append("УТМ: ").AppendLine(m.Utm);
        if (!string.IsNullOrWhiteSpace(m.Fsrar)) sb.Append("ФСРАР: ").AppendLine(m.Fsrar);
        if (m.Port is int p) sb.Append("Порт: ").AppendLine(p.ToString());
        if (!string.IsNullOrWhiteSpace(m.Machine)) sb.Append("Машина: ").AppendLine(m.Machine);
        sb.Append("Время: ").AppendLine(m.When.ToString("dd.MM.yyyy HH:mm:ss"));
        if (m.Extra is not null) foreach (var (k, v) in m.Extra) sb.Append(k).Append(": ").AppendLine(v);
        if (!string.IsNullOrWhiteSpace(m.Hint)) sb.AppendLine().Append("Что делать: ").AppendLine(m.Hint);
        if (!string.IsNullOrWhiteSpace(m.PanelUrl)) sb.AppendLine().Append("Панель: ").AppendLine(m.PanelUrl);
        if (!string.IsNullOrWhiteSpace(m.UtmUrl)) sb.Append("УТМ: ").AppendLine(m.UtmUrl);
        return sb.ToString().TrimEnd();
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        return i < 0 ? s : s[..i];
    }
}
