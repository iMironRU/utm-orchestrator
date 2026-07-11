using System.Threading;

namespace UtmOrchestrator.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Один экземпляр на пользователя: если трей уже запущен (Run-ключ + задача
        // «при входе» — оба для надёжности), второй сразу выходит, иконка не двоится.
        using var mutex = new Mutex(true, "Local\\UtmOrchestratorTray_SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}
