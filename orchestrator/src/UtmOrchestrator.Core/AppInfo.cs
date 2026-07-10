using System.Reflection;

namespace UtmOrchestrator.Core;

/// <summary>
/// Единое имя продукта и версия для трея, окна и заголовков. Версия берётся из
/// сборки запущенного exe (AssemblyVersion задаётся в Directory.Build.props).
/// </summary>
public static class AppInfo
{
    /// <summary>Короткое имя продукта.</summary>
    public const string ProductName = "УТМ:Оркестратор";

    /// <summary>Версия вида "0.0.0.1" из сборки исполняемого приложения.</summary>
    public static string Version { get; } = ComputeVersion();

    /// <summary>Полное имя с версией: «УТМ:Оркестратор (v 0.0.0.1)».</summary>
    public static string Title => $"{ProductName} (v {Version})";

    private static string ComputeVersion()
    {
        var v = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetName().Version ?? new Version(0, 0, 0, 1);
        return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
    }
}
