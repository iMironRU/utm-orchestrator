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
    private readonly System.Windows.Forms.Timer _heartbeat;
    private readonly JobPoller _jobPoller;
    private readonly Icon _ok, _error, _busyIcon, _disconnected;
    private MainForm? _form;
    private bool _busy;

    // «Пульс»: последняя успешная сводка + когда получена — чтобы всегда показывать,
    // что трей живо мониторит («обновлено N сек назад»), а не завис.
    private string _lastSummary = "загрузка…";
    private DateTime _lastOkUtc = DateTime.UtcNow;
    private bool _everConnected;

    private const int FastPollMs = 2500;   // пока не «Ок» (загрузка/подъём/сбой)
    private const int IdlePollMs = 8000;   // в устойчивом «Ок»

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

        // Адаптивный опрос: часто, пока не «Ок» (загрузка/подъём/сбой) — чтобы ловить
        // готовность за пару секунд, а не ждать полный интервал; в покое — реже.
        _timer = new System.Windows.Forms.Timer { Interval = FastPollMs };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        // «Пульс»: раз в секунду обновляем подсказку с «обновлено N сек назад», даже
        // между опросами — чтобы было видно, что трей живой и мониторит.
        _heartbeat = new System.Windows.Forms.Timer { Interval = 1000 };
        _heartbeat.Tick += (_, _) => UpdateTooltip();
        _heartbeat.Start();

        // «Руки» веба: опрос интерактивных заданий (скан токенов, лечение).
        _jobPoller = new JobPoller();

        _ = RefreshAsync();
    }

    // Подсказка = продукт + сводка + «пульс» (сколько назад обновлялось).
    private void UpdateTooltip()
    {
        int ago = (int)Math.Max(0, (DateTime.UtcNow - _lastOkUtc).TotalSeconds);
        string beat = !_everConnected ? "подключаюсь…"
                    : ago < 2 ? "обновлено только что"
                    : $"обновлено {ago}с назад";
        _notify.Text = Truncate($"{AppInfo.Title} — {_lastSummary} · {beat}", 63);
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var snap = await StatusProvider.GetAsync();

            // 4 дизайнерских состояния иконки: ok / error / busy / disconnected.
            // Ok → ok; Starting (идёт подъём) → busy; Warn и Fault → error (нужно
            // внимание); нет данных / служба стоит → disconnected.
            _notify.Icon = snap.Overall switch
            {
                OverallStatus.Ok => _ok,
                OverallStatus.Starting => _busyIcon,
                OverallStatus.Warn => _error,
                OverallStatus.Fault => _error,
                _ => _disconnected,
            };

            // Свежая сводка получена — обновляем «пульс» и подсказку.
            _lastSummary = snap.Summary;
            _lastOkUtc = DateTime.UtcNow;
            _everConnected = true;
            UpdateTooltip();

            _form?.UpdateSnapshot(snap);

            // Пока не «Ок» (идёт подъём/сбой/загрузка) — опрашиваем часто, чтобы
            // поймать готовность за пару секунд; в устойчивом «Ок» — реже.
            int want = snap.Overall == OverallStatus.Ok ? IdlePollMs : FastPollMs;
            if (_timer.Interval != want) _timer.Interval = want;
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
        _heartbeat.Stop();
        _jobPoller.Dispose();
        _notify.Visible = false;
        _notify.Dispose();
        _form?.Dispose();
        ExitThread();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
