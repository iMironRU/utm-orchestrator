using System.Net.Http;
using System.Text;

namespace UtmOrchestrator.Core.Alerts;

/// <summary>Канал MAX (мессенджер МАКС, Bot API). Отечественный сервис — ходим НАПРЯМУЮ (без GitHub-прокси).
/// Эндпоинт Bot API: POST https://botapi.max.ru/messages?access_token=TOKEN&amp;chat_id=ID, тело {"text":...}.
/// ⚠ API MAX молодой — при первом тесте с реальным токеном сверить формат (может отличаться chat_id/recipient_id).</summary>
public sealed class MaxAlertChannel : IAlertChannel
{
    private readonly AlertSettings.MaxSettings _s;
    public MaxAlertChannel(AlertSettings.MaxSettings s) => _s = s;

    public string Name => "max";
    public bool Configured => !string.IsNullOrWhiteSpace(_s.BotTokenEnc);
    public bool Enabled => _s.Enabled && Configured && !string.IsNullOrWhiteSpace(_s.ChatId);

    /// <summary>Доступность botapi.max.ru (напрямую) + валидность токена (GET /me). Сообщение не шлём.</summary>
    public async Task<(bool Ok, string Detail)> CheckAsync(CancellationToken ct = default)
    {
        string? token = AlertSettings.Unprotect(_s.BotTokenEnc);
        try
        {
            using var h = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(12) };
            if (string.IsNullOrEmpty(token))
            {
                using var r0 = await h.GetAsync("https://botapi.max.ru/", ct).ConfigureAwait(false);
                return (true, "botapi.max.ru доступен (напрямую); токен не задан");
            }
            using var r = await h.GetAsync($"https://botapi.max.ru/me?access_token={Uri.EscapeDataString(token)}", ct).ConfigureAwait(false);
            string body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (r.IsSuccessStatusCode)
            {
                var m = System.Text.RegularExpressions.Regex.Match(body, "\"(?:username|name)\":\"([^\"]+)\"");
                return (true, $"доступен (напрямую), бот {(m.Success ? m.Groups[1].Value : "ок")}");
            }
            if ((int)r.StatusCode == 401 || (int)r.StatusCode == 403)
                return (false, $"botapi.max.ru доступен, но токен отклонён (HTTP {(int)r.StatusCode})");
            return (false, $"botapi.max.ru ответил HTTP {(int)r.StatusCode}: {(body.Length > 120 ? body[..120] : body)}");
        }
        catch (Exception e)
        {
            return (false, $"botapi.max.ru недоступен: {e.GetBaseException().Message}");
        }
    }

    public async Task<string?> SendAsync(AlertMessage msg, CancellationToken ct = default)
    {
        string? token = AlertSettings.Unprotect(_s.BotTokenEnc);
        if (string.IsNullOrEmpty(token)) return "max: нет токена";
        try
        {
            using var h = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(15) };

            string text = EmailHtml.RenderText(msg).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
            var json = new StringContent($"{{\"text\":\"{text}\"}}", Encoding.UTF8, "application/json");
            string url = $"https://botapi.max.ru/messages?access_token={Uri.EscapeDataString(token)}&chat_id={Uri.EscapeDataString(_s.ChatId!)}";
            var resp = await h.PostAsync(url, json, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return null;
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return $"max: HTTP {(int)resp.StatusCode} {body}".Trim();
        }
        catch (Exception e)
        {
            return $"max: {e.Message}";
        }
    }
}
