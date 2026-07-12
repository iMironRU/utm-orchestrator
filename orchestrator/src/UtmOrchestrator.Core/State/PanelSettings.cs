using System.Security.Cryptography;
using System.Text.Json;

namespace UtmOrchestrator.Core.State;

/// <summary>Настройки веб-панели (persist в %ProgramData%\UtmOrchestrator\settings.json).</summary>
public sealed class PanelSettingsData
{
    /// <summary>Требовать вход в панель («по галочке»).</summary>
    public bool RequireAuth { get; set; }

    /// <summary>Имя пользователя для входа.</summary>
    public string? Username { get; set; }

    /// <summary>Хэш пароля (PBKDF2) в base64 и соль — сам пароль не храним.</summary>
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }

    /// <summary>Доступ к панели по сети (иначе только localhost). Требует пароль.</summary>
    public bool NetworkAccess { get; set; }

    /// <summary>Список IP, с которых панель доступна (пусто = любой, но с паролем).</summary>
    public List<string> AllowedIps { get; set; } = new();

    /// <summary>Задан ли пароль (для UI — сам пароль/хэш не отдаём).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash) && !string.IsNullOrEmpty(PasswordSalt);
}

/// <summary>Хэширование/проверка пароля панели (PBKDF2-SHA256).</summary>
public static class PanelPassword
{
    private const int Iterations = 100_000, SaltLen = 16, HashLen = 32;

    public static (string Hash, string Salt) Make(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLen);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashLen);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string? hashB64, string? saltB64)
    {
        if (string.IsNullOrEmpty(hashB64) || string.IsNullOrEmpty(saltB64)) return false;
        try
        {
            byte[] salt = Convert.FromBase64String(saltB64);
            byte[] expected = Convert.FromBase64String(hashB64);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }
}

public sealed class PanelSettings
{
    public static string DefaultPath => AppPaths.Data("settings.json");

    private readonly string _path;
    private readonly object _lock = new();
    private PanelSettingsData? _cached;

    public PanelSettings(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>Кэшированные настройки для горячего пути (middleware). Обновляются при Save.</summary>
    public PanelSettingsData Current => _cached ??= Load();

    public PanelSettingsData Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    _cached = JsonSerializer.Deserialize<PanelSettingsData>(File.ReadAllText(_path)) ?? new PanelSettingsData();
                else
                    _cached = new PanelSettingsData();
            }
            catch { _cached = new PanelSettingsData(); }
            return _cached;
        }
    }

    public void Save(PanelSettingsData data)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // нормализуем IP-список
            data.AllowedIps = (data.AllowedIps ?? new List<string>())
                .Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).Distinct().ToList();
            File.WriteAllText(_path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            _cached = data;
        }
    }
}
