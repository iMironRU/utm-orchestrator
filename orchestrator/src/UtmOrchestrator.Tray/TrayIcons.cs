using System.Drawing;
using System.Drawing.Drawing2D;

namespace UtmOrchestrator.Tray;

/// <summary>Генерирует простые цветные иконки-индикаторы для трея (без .ico-ассетов).</summary>
internal static class TrayIcons
{
    public static Icon Make(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 2, 2, 11, 11);
            using var pen = new Pen(Color.FromArgb(70, 0, 0, 0));
            g.DrawEllipse(pen, 2, 2, 11, 11);
        }
        // HICON живёт весь срок жизни приложения (иконки кэшируются) — утечки нет.
        return Icon.FromHandle(bmp.GetHicon());
    }
}
