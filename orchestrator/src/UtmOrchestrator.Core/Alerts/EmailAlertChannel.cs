using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace UtmOrchestrator.Core.Alerts;

/// <summary>Канал Email (SMTP) через MailKit — поддерживает и implicit SSL (465), и STARTTLS (587).
/// Пароль берётся расшифрованным из настроек (DPAPI). SMTP идёт НАПРЯМУЮ (не через GitHub-прокси).</summary>
public sealed class EmailAlertChannel : IAlertChannel
{
    private readonly AlertSettings.EmailSettings _s;
    public EmailAlertChannel(AlertSettings.EmailSettings s) => _s = s;

    public string Name => "email";
    public bool Configured => !string.IsNullOrWhiteSpace(_s.Host);
    public bool Enabled => _s.Enabled && Configured
        && !string.IsNullOrWhiteSpace(_s.From)
        && _s.To.Count > 0;

    /// <summary>Реальное SMTP-подключение (+авторизация, если задан пароль) без отправки письма.</summary>
    public async Task<(bool Ok, string Detail)> CheckAsync(CancellationToken ct = default)
    {
        if (!Configured) return (false, "SMTP-хост не задан");
        try
        {
            using var client = new SmtpClient { Timeout = 12000 };
            var secure = _s.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(_s.Host!, _s.Port, secure, ct).ConfigureAwait(false); // Host гарантирован Configured
            string? pass = AlertSettings.Unprotect(_s.PasswordEnc);
            string detail = $"{_s.Host}:{_s.Port} ({(_s.UseSsl ? "SSL" : "STARTTLS")}) — подключение ок";
            if (!string.IsNullOrEmpty(pass) && !string.IsNullOrWhiteSpace(_s.From))
            {
                await client.AuthenticateAsync(_s.From!, pass, ct).ConfigureAwait(false);
                detail += ", авторизация ок";
            }
            else detail += "; пароль не задан — авторизация не проверялась";
            await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            return (true, detail);
        }
        catch (MailKit.Security.AuthenticationException e)
        {
            return (false, $"{_s.Host}:{_s.Port} доступен, но авторизация отклонена: {e.Message}");
        }
        catch (Exception e)
        {
            return (false, $"{_s.Host}:{_s.Port} недоступен: {e.GetBaseException().Message}");
        }
    }

    public async Task<string?> SendAsync(AlertMessage msg, CancellationToken ct = default)
    {
        try
        {
            var mime = new MimeMessage();
            mime.From.Add(MailboxAddress.Parse(_s.From!)); // From/Host гарантированы Enabled
            foreach (var to in _s.To.Where(t => !string.IsNullOrWhiteSpace(t)))
                mime.To.Add(MailboxAddress.Parse(to.Trim()));
            mime.Subject = msg.Title;
            // multipart/alternative: HTML-карточка + plain-text для клиентов без HTML
            mime.Body = new BodyBuilder
            {
                TextBody = EmailHtml.RenderText(msg),
                HtmlBody = EmailHtml.Render(msg),
            }.ToMessageBody();

            using var client = new SmtpClient();
            var secure = _s.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(_s.Host!, _s.Port, secure, ct).ConfigureAwait(false);
            string? pass = AlertSettings.Unprotect(_s.PasswordEnc);
            if (!string.IsNullOrEmpty(pass))
                await client.AuthenticateAsync(_s.From!, pass, ct).ConfigureAwait(false);
            await client.SendAsync(mime, ct).ConfigureAwait(false);
            await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception e)
        {
            return $"email: {e.Message}";
        }
    }
}
