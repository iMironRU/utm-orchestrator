using System.Text.Json;
using System.Text.Json.Serialization;

namespace UtmOrchestrator.Core.State;

/// <summary>Единый source of truth оркестратора (сериализуется в JSON).</summary>
public sealed class OrchestratorState
{
    public List<UtmInstance> Instances { get; set; } = new();

    /// <summary>
    /// Ссылка на хранилище PIN (не сам PIN!). Реальный PIN хранится защищённо
    /// (DPAPI/шифрованный файл), здесь только идентификатор записи.
    /// </summary>
    public string? PinRef { get; set; }

    /// <summary>%ProgramData%\UtmOrchestrator\state.json — привязки служба↔порт↔токен.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UtmOrchestrator", "state.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static OrchestratorState Load(string path)
    {
        if (!File.Exists(path)) return new OrchestratorState();
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<OrchestratorState>(json, JsonOpts) ?? new OrchestratorState();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}
