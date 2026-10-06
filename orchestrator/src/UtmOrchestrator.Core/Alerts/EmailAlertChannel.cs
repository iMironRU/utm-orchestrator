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
    public bool Enabled => _s.Enabled
        && !string.IsNullOrWhiteSpace(_s.Host)
        && !string.IsNullOrWhiteSpace(_s.From)
        && _s.To.Count > 0;

    public async Task<string?> SendAsync(AlertMessage msg, CancellationToken ct = default)
    {
        try
        {
            var mime = new MimeMessage();
            mime.From.Add(MailboxAddress.Parse(_s.From));
            foreach (var to in _s.To.Where(t => !string.IsNullOrWhiteSpace(t)))
                mime.To.Add(MailboxAddress.Parse(to.Trim()));
            mime.Subject = msg.Title;
            mime.Body = new TextPart("plain") { Text = msg.Body };

            using var client = new SmtpClient();
            var secure = _s.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(_s.Host, _s.Port, secure, ct).ConfigureAwait(false);
            string? pass = AlertSettings.Unprotect(_s.PasswordEnc);
            if (!string.IsNullOrEmpty(pass))
                await client.AuthenticateAsync(_s.From, pass, ct).ConfigureAwait(false);
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
