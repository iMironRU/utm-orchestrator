using System.Text.Json;

namespace UtmOrchestrator.Core.State;

/// <summary>Настройки веб-панели (persist в %ProgramData%\UtmOrchestrator\settings.json).</summary>
public sealed class PanelSettingsData
{
    /// <summary>Требовать вход в панель («по галочке»).</summary>
    public bool RequireAuth { get; set; }

    /// <summary>Имя пользователя для входа (пароль хранится отдельно/хэш — не здесь).</summary>
    public string? Username { get; set; }

    /// <summary>Список IP, с которых панель доступна (пусто = только localhost).</summary>
    public List<string> AllowedIps { get; set; } = new();
}

public sealed class PanelSettings
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UtmOrchestrator", "settings.json");

    private readonly string _path;
    private readonly object _lock = new();

    public PanelSettings(string? path = null) => _path = path ?? DefaultPath;

    public PanelSettingsData Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    return JsonSerializer.Deserialize<PanelSettingsData>(File.ReadAllText(_path)) ?? new PanelSettingsData();
            }
            catch { /* повреждён — дефолт */ }
            return new PanelSettingsData();
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
        }
    }
}
