namespace UtmOrchestrator.Core;

/// <summary>
/// Единые пути данных — всё рядом с exe, в одной папке: <app>\data (и <app>\data\logs).
/// Т.е. вся установка живёт в C:\UtmOrchestrator: программа + wwwroot + data.
/// Служба (LocalSystem), CLI (admin) и трей (пользователь) запускаются из одной папки,
/// поэтому AppContext.BaseDirectory у всех = каталог установки, и data-путь общий.
/// </summary>
public static class AppPaths
{
    /// <summary>Корень данных: <app>\data.</summary>
    public static string DataDir { get; } = Init(Path.Combine(AppContext.BaseDirectory, "data"));

    /// <summary>Логи: <app>\data\logs.</summary>
    public static string LogsDir { get; } = Init(Path.Combine(DataDir, "logs"));

    /// <summary>Файл данных по имени (state.json, serials.json, …).</summary>
    public static string Data(string name) => Path.Combine(DataDir, name);

    /// <summary>Файл лога по имени.</summary>
    public static string Log(string name) => Path.Combine(LogsDir, name);

    /// <summary>Основной журнал операций.</summary>
    public static string BringupLog => Log("bringup.log");

    private static string Init(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* создастся при первой записи */ }
        return dir;
    }
}
