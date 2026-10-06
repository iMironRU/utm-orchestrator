using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UtmOrchestrator.Core.Alerts;

/// <summary>
/// Настройки уведомлений о сбоях: какие события слать + параметры каналов. Хранится в
/// &lt;root&gt;\data\alerts.json. СЕКРЕТЫ (пароль SMTP, токены ботов) лежат зашифрованными DPAPI
/// (<see cref="Protect"/>/<see cref="Unprotect"/>, scope LocalMachine) — НЕ в открытом виде, НЕ в git,
/// НЕ в логах. В API наружу секреты не отдаются (см. <see cref="Redacted"/>).
/// </summary>
public sealed class AlertSettings
{
    public bool Enabled { get; set; }

    // Какие события уведомлять (настраивается оператором).
    public bool OnFaulty { get; set; } = true;
    public bool OnNeedRsa { get; set; } = true;
    public bool OnSigningBroken { get; set; } = true;
    public bool OnSigningUnconfirmed { get; set; }
    public bool OnRecovery { get; set; } = true;

    /// <summary>Не слать повторно о той же проблеме чаще, чем раз в N минут.</summary>
    public int CooldownMinutes { get; set; } = 30;

    public EmailSettings Email { get; set; } = new();
    public TelegramSettings Telegram { get; set; } = new();
    public MaxSettings Max { get; set; } = new();

    public bool WantsKind(AlertKind k) => k switch
    {
        AlertKind.Faulty => OnFaulty,
        AlertKind.NeedRsa => OnNeedRsa,
        AlertKind.SigningBroken => OnSigningBroken,
        AlertKind.SigningUnconfirmed => OnSigningUnconfirmed,
        AlertKind.Recovery => OnRecovery,
        _ => false,
    };

    public sealed class EmailSettings
    {
        public bool Enabled { get; set; }
        public string? Host { get; set; }
        public int Port { get; set; } = 465;
        public bool UseSsl { get; set; } = true;   // implicit SSL (465); false = STARTTLS (587)
        public string? From { get; set; }
        public string? PasswordEnc { get; set; }   // DPAPI
        public List<string> To { get; set; } = new();
    }

    public sealed class TelegramSettings
    {
        public bool Enabled { get; set; }
        public string? BotTokenEnc { get; set; }   // DPAPI
        public string? ChatId { get; set; }
    }

    public sealed class MaxSettings
    {
        public bool Enabled { get; set; }
        public string? BotTokenEnc { get; set; }   // DPAPI
        public string? ChatId { get; set; }
    }

    // --- Хранилище ---

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => AppPaths.Data("alerts.json");

    public static AlertSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new AlertSettings();
            return JsonSerializer.Deserialize<AlertSettings>(File.ReadAllText(path)) ?? new AlertSettings();
        }
        catch { return new AlertSettings(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>Копия без секретов — для отдачи в UI (секреты заменены маркером наличия).</summary>
    public object Redacted() => new
    {
        Enabled,
        OnFaulty, OnNeedRsa, OnSigningBroken, OnSigningUnconfirmed, OnRecovery,
        CooldownMinutes,
        Email = new { Email.Enabled, Email.Host, Email.Port, Email.UseSsl, Email.From, Email.To, HasPassword = !string.IsNullOrEmpty(Email.PasswordEnc) },
        Telegram = new { Telegram.Enabled, Telegram.ChatId, HasToken = !string.IsNullOrEmpty(Telegram.BotTokenEnc) },
        Max = new { Max.Enabled, Max.ChatId, HasToken = !string.IsNullOrEmpty(Max.BotTokenEnc) },
    };

    // --- DPAPI (Windows). На не-Windows возвращаем как есть (dev). ---

    [SupportedOSPlatform("windows")]
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        if (!OperatingSystem.IsWindows()) return plain;
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(enc);
    }

    public static string? Unprotect(string? enc)
    {
        if (string.IsNullOrEmpty(enc)) return null;
        try
        {
            if (!OperatingSystem.IsWindows()) return enc;
            var dec = ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return null; }
    }
}
