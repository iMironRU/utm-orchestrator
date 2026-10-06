using System.Net;
using System.Net.Http;
using UtmOrchestrator.Core.Update;

namespace UtmOrchestrator.Core.Alerts;

/// <summary>Канал Telegram (Bot API). api.telegram.org в РФ часто доступен только через прокси —
/// берём тот же прокси, что и для GitHub (<see cref="GitHubProxy"/>). Токен — расшифрованный из настроек.</summary>
public sealed class TelegramAlertChannel : IAlertChannel
{
    private readonly AlertSettings.TelegramSettings _s;
    public TelegramAlertChannel(AlertSettings.TelegramSettings s) => _s = s;

    public string Name => "telegram";
    public bool Configured => !string.IsNullOrWhiteSpace(_s.BotTokenEnc);
    public bool Enabled => _s.Enabled && Configured && !string.IsNullOrWhiteSpace(_s.ChatId);

    private static HttpClient NewClient(out string proxySource)
    {
        var (proxy, src) = GitHubProxy.Resolve();
        proxySource = src;
        return new HttpClient(new SocketsHttpHandler
        {
            UseProxy = proxy is not null,
            Proxy = proxy,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        })
        { Timeout = TimeSpan.FromSeconds(12) };
    }

    /// <summary>Доступность api.telegram.org через прокси + валидность токена (getMe). Сообщение не шлём.</summary>
    public async Task<(bool Ok, string Detail)> CheckAsync(CancellationToken ct = default)
    {
        string? token = AlertSettings.Unprotect(_s.BotTokenEnc);
        try
        {
            using var h = NewClient(out var via);
            string path = via.Contains("нет", StringComparison.OrdinalIgnoreCase) || via.Contains("без", StringComparison.OrdinalIgnoreCase)
                ? "напрямую" : "через прокси " + via;
            if (string.IsNullOrEmpty(token))
            {
                // Токена нет — проверяем только достижимость сервера (любой HTTP-ответ = доступен).
                using var r0 = await h.GetAsync("https://api.telegram.org/", ct).ConfigureAwait(false);
                return (true, $"api.telegram.org доступен ({path}); токен не задан");
            }
            using var r = await h.GetAsync($"https://api.telegram.org/bot{token}/getMe", ct).ConfigureAwait(false);
            string body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (r.IsSuccessStatusCode && body.Contains("\"ok\":true"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(body, "\"username\":\"([^\"]+)\"");
                return (true, $"доступен ({path}), бот @{(m.Success ? m.Groups[1].Value : "?")}");
            }
            if ((int)r.StatusCode == 401 || (int)r.StatusCode == 404)
                return (false, $"api.telegram.org доступен ({path}), но токен отклонён (HTTP {(int)r.StatusCode})");
            return (false, $"api.telegram.org ответил HTTP {(int)r.StatusCode} ({path})");
        }
        catch (Exception e)
        {
            var (_, via) = GitHubProxy.Resolve();
            return (false, $"api.telegram.org недоступен (прокси: {via}): {e.GetBaseException().Message}");
        }
    }

    public async Task<string?> SendAsync(AlertMessage msg, CancellationToken ct = default)
    {
        string? token = AlertSettings.Unprotect(_s.BotTokenEnc);
        if (string.IsNullOrEmpty(token)) return "telegram: нет токена";
        try
        {
            using var h = NewClient(out _);

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", _s.ChatId!),
                new KeyValuePair<string, string>("text", msg.Title + "\n\n" + msg.Body),
                new KeyValuePair<string, string>("disable_web_page_preview", "true"),
            });
            var resp = await h.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", form, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return null;
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return $"telegram: HTTP {(int)resp.StatusCode} {body}".Trim();
        }
        catch (Exception e)
        {
            return $"telegram: {e.Message}";
        }
    }
}
