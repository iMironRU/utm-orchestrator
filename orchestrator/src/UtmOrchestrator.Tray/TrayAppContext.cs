using System.Drawing;
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
    private readonly Icon _green, _amber, _red, _gray;
    private MainForm? _form;
    private bool _busy;

    public TrayAppContext()
    {
        _green = TrayIcons.Make(Color.FromArgb(46, 125, 50));
        _amber = TrayIcons.Make(Color.FromArgb(249, 168, 37));
        _red = TrayIcons.Make(Color.FromArgb(198, 40, 40));
        _gray = TrayIcons.Make(Color.FromArgb(140, 140, 140));

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowWindow());
        menu.Items.Add("Обновить", null, async (_, _) => await RefreshAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        _notify = new NotifyIcon
        {
            Icon = _gray,
            Text = "UTM Orchestrator — загрузка…",
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

            _notify.Icon = snap.Overall switch
            {
                OverallStatus.Ok => _green,
                OverallStatus.Warn => _amber,
                OverallStatus.Fault => _red,
                _ => _gray,
            };

            string svc = snap.OrchestratorService == ServiceState.NotInstalled
                ? "служба не уст."
                : $"служба: {snap.OrchestratorService}";
            _notify.Text = Truncate($"UTM Orchestrator — {snap.Summary} ({svc})", 63);

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
