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
    public bool Enabled => _s.Enabled
        && !string.IsNullOrWhiteSpace(_s.BotTokenEnc)
        && !string.IsNullOrWhiteSpace(_s.ChatId);

    public async Task<string?> SendAsync(AlertMessage msg, CancellationToken ct = default)
    {
        string? token = AlertSettings.Unprotect(_s.BotTokenEnc);
        if (string.IsNullOrEmpty(token)) return "telegram: нет токена";
        try
        {
            var (proxy, _) = GitHubProxy.Resolve();
            using var h = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = proxy is not null,
                Proxy = proxy,
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            })
            { Timeout = TimeSpan.FromSeconds(15) };

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
