using System.Drawing;

namespace UtmOrchestrator.Tray;

/// <summary>Загружает дизайнерские иконки трея из встроенных ресурсов (PNG → Icon).</summary>
internal static class TrayIcons
{
    /// <param name="name">tray-ok | tray-error | tray-busy | tray-disconnected</param>
    public static Icon Load(string name)
    {
        var asm = typeof(TrayIcons).Assembly;
        using var stream = asm.GetManifestResourceStream(name + ".png")
            ?? throw new FileNotFoundException($"Встроенная иконка не найдена: {name}.png");
        using var bmp = new Bitmap(stream);
        // HICON живёт весь срок жизни приложения (иконки кэшируются) — утечки нет.
        return Icon.FromHandle(bmp.GetHicon());
    }
}
