using System.Text.Json;

namespace UtmOrchestrator.Core.State;

/// <summary>
/// Кэш соответствия ФСРАР-ID → серийник токена. Заполняется РЕДКО (при явном
/// скане токенов в окне обслуживания) и хранится в
/// %ProgramData%\UtmOrchestrator\serials.json.
///
/// ВАЖНО (рабочая машина): нативный драйвер rtPKCS11ECP.dll при C_GetTokenInfo на
/// токене, который прямо сейчас занят живым УТМ, может отдать
/// AccessViolationException. В .NET Core это исключение НЕ ловится и роняет весь
/// процесс. Поэтому опрос статуса НЕ сканирует токены — он читает соответствие из
/// этого кэша. Скан выполняется только по явной команде (обслуживание).
/// Потокобезопасно.
/// </summary>
public sealed class SerialCache
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UtmOrchestrator", "serials.json");

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _map; // fsrar -> serial

    public SerialCache(string? path = null)
    {
        _path = path ?? DefaultPath;
        _map = Load(_path);
    }

    /// <summary>Серийник токена по ФСРАР-ID (или null, если не выучен).</summary>
    public string? GetSerial(string? fsrar)
    {
        if (string.IsNullOrEmpty(fsrar)) return null;
        lock (_lock) return _map.TryGetValue(fsrar, out var s) ? s : null;
    }

    /// <summary>
    /// Запомнить соответствие. Пишет на диск только если что-то реально изменилось.
    /// </summary>
    public void Learn(string fsrar, string serial)
    {
        if (string.IsNullOrEmpty(fsrar) || string.IsNullOrEmpty(serial)) return;
        lock (_lock)
        {
            if (_map.TryGetValue(fsrar, out var cur) && cur == serial) return;
            _map[fsrar] = serial;
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
