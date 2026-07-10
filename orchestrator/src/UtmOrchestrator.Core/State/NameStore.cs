using System.Text.Json;

namespace UtmOrchestrator.Core.State;

/// <summary>
/// Кастомные краткие имена УТМ, заданные пользователем в интерфейсе. Ключ —
/// серийник токена (стабильный, не зависит от порта/переустановки). Хранится
/// в %ProgramData%\UtmOrchestrator\names.json. Потокобезопасно.
/// </summary>
public sealed class NameStore
{
    public static string DefaultPath => AppPaths.Data("names.json");

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _map;

    public NameStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        _map = Load(_path);
    }

    public string? Get(string? serial)
    {
        if (string.IsNullOrEmpty(serial)) return null;
        lock (_lock) return _map.TryGetValue(serial, out var n) ? n : null;
    }

    public void Set(string serial, string? name)
    {
        if (string.IsNullOrEmpty(serial)) return;
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(name)) _map.Remove(serial);
            else _map[serial] = name.Trim();
            Save();
        }
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (loaded is not null)
                    return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* повреждённый/недоступный файл — начинаем с пустого */ }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }));
    }
}
