using System.Text.Json;

namespace UtmOrchestrator.Service;

/// <summary>
/// История длительностей boot-подъёма (сек) в data\boot-history.json. По ней считаем
/// прогноз «готово через ~M:SS» на следующей загрузке (медиана последних запусков —
/// устойчивее среднего к разовым выбросам).
/// </summary>
public static class BootHistory
{
    private const int Keep = 10;
    private static string FilePath => UtmOrchestrator.Core.AppPaths.Data("boot-history.json");

    /// <summary>Медиана прошлых длительностей (сек), 0 — если истории нет.</summary>
    public static int MedianSeconds()
    {
        var list = Load();
        if (list.Count == 0) return 0;
        var sorted = list.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>Записать длительность завершившегося подъёма (сек).</summary>
    public static void Record(int seconds)
    {
        if (seconds is <= 0 or > 3600) return; // отбрасываем мусор
        var list = Load();
        list.Add(seconds);
        if (list.Count > Keep) list = list.Skip(list.Count - Keep).ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
        }
        catch { /* не критично */ }
    }

    private static List<int> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<int>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* повреждённый файл — начнём заново */ }
        return new();
    }
}
