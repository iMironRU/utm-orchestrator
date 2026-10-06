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
    public bool Enabled => _s.Enabled
        && !string.IsNullOrWhiteSpace(_s.BotTokenEnc)
        && !string.IsNullOrWhiteSpace(_s.ChatId);

    public async Task<string?> SendAsync(AlertMessage msg, CancellationToken ct = default)
    {
        string? token = AlertSettings.Unprotect(_s.BotTokenEnc);
        if (string.IsNullOrEmpty(token)) return "max: нет токена";
        try
        {
            using var h = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(15) };

            string text = (msg.Title + "\n\n" + msg.Body).Replace("\"", "\\\"").Replace("\n", "\\n");
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
