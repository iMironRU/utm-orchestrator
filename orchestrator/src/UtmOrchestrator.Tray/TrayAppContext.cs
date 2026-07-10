using System.Drawing;
using UtmOrchestrator.Core;
using UtmOrchestrator.Core.Services;

namespace UtmOrchestrator.Tray;

/// <summary>
/// Живёт в трее: иконка со сводным статусом (зелёная/жёлтая/красная/серая),
/// меню быстрых действий, окно-заглушка. Периодически обновляет статус.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _notify;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Icon _ok, _error, _busyIcon, _disconnected;
    private MainForm? _form;
    private bool _busy;

    public TrayAppContext()
    {
        _ok = TrayIcons.Load("tray-ok");
        _error = TrayIcons.Load("tray-error");
        _busyIcon = TrayIcons.Load("tray-busy");
        _disconnected = TrayIcons.Load("tray-disconnected");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowWindow());
        menu.Items.Add("Обновить", null, async (_, _) => await RefreshAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        _notify = new NotifyIcon
        {
            Icon = _disconnected,
            Text = Truncate($"{AppInfo.Title} — загрузка…", 63),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notify.DoubleClick += (_, _) => ShowWindow();

        _timer = new System.Windows.Forms.Timer { Interval = 8000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var snap = await StatusProvider.GetAsync();

            // 4 дизайнерских состояния иконки: ok / error / busy / disconnected.
            // Ok → ok; Warn и Fault → error (нужно внимание); нет данных → disconnected.
            _notify.Icon = snap.Overall switch
            {
                OverallStatus.Ok => _ok,
                OverallStatus.Warn => _error,
                OverallStatus.Fault => _error,
                _ => _disconnected,
            };

            // Tooltip: имя продукта с версией + краткая сводка по УТМ.
            _notify.Text = Truncate($"{AppInfo.Title} — {snap.Summary}", 63);

            _form?.UpdateSnapshot(snap);
        }
        catch
        {
            // ошибки обновления не должны валить трей
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowWindow()
    {
        if (_form is null || _form.IsDisposed)
        {
            _form = new MainForm();
            _form.RefreshRequested += async () => await RefreshAsync();
        }
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.BringToFront();
        _form.Activate();
        _ = RefreshAsync();
    }

    private void ExitApp()
    {
        _timer.Stop();
        _notify.Visible = false;
        _notify.Dispose();
        _form?.Dispose();
        ExitThread();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
