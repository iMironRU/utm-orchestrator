using System.Drawing;
using UtmOrchestrator.Core.Health;
using UtmOrchestrator.Core.Services;

namespace UtmOrchestrator.Tray;

/// <summary>
/// Окно-заглушка панели. Показывает «тут будет интерфейс» + РЕАЛЬНЫЙ статус
/// (служба оркестратора + здоровье всех УТМ), чтобы уже было полезно.
/// Закрытие крестиком прячет окно (приложение живёт в трее).
/// </summary>
public sealed class MainForm : Form
{
    private readonly Label _serviceLabel;
    private readonly Label _summaryLabel;
    private readonly ListView _list;

    public event Action? RefreshRequested;

    public MainForm()
    {
        Text = "UTM Orchestrator";
        Width = 760;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));  // header
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // service
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));  // summary
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // list
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));  // button

        var header = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Тут будет интерфейс",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 20F, FontStyle.Bold),
            ForeColor = Color.FromArgb(110, 110, 110),
        };

        _serviceLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Служба оркестратора: —",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 0),
        };

        _summaryLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Статус: —",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 0),
            ForeColor = Color.Gray,
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("УТМ", 110);
        _list.Columns.Add("Порт", 60);
        _list.Columns.Add("ФСРАР", 130);
        _list.Columns.Add("Состояние", 110);
        _list.Columns.Add("Причина", 320);

        var refreshBtn = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Обновить",
            Margin = new Padding(10, 6, 10, 8),
        };
        refreshBtn.Click += (_, _) => RefreshRequested?.Invoke();

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_serviceLabel, 0, 1);
        root.Controls.Add(_summaryLabel, 0, 2);
        root.Controls.Add(_list, 0, 3);
        root.Controls.Add(refreshBtn, 0, 4);
        Controls.Add(root);
    }

    public void UpdateSnapshot(StatusSnapshot s)
    {
        _serviceLabel.Text = "Служба оркестратора: " + (s.OrchestratorService == ServiceState.NotInstalled
            ? "не установлена (будет установлена позже)"
            : ServiceStateRu(s.OrchestratorService));

        _summaryLabel.Text = "Статус УТМ: " + s.Summary;
        _summaryLabel.ForeColor = s.Overall switch
        {
            OverallStatus.Ok => Color.FromArgb(46, 125, 50),
            OverallStatus.Warn => Color.FromArgb(180, 120, 0),
            OverallStatus.Fault => Color.FromArgb(198, 40, 40),
            _ => Color.Gray,
        };

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var h in s.Utms)
        {
            var item = new ListViewItem(new[]
            {
                h.Instance.ServiceName,
                h.Instance.Port > 0 ? h.Instance.Port.ToString() : "-",
                h.Info?.OwnerId ?? h.Instance.ExpectedFsrar ?? "-",
                VerdictRu(h.Verdict),
                h.Reason ?? string.Empty,
            });
            item.ForeColor = h.Verdict switch
            {
                HealthVerdict.Ok => Color.FromArgb(46, 125, 50),
                HealthVerdict.Faulty => Color.FromArgb(198, 40, 40),
                HealthVerdict.Stopped => Color.Gray,
                _ => Color.FromArgb(180, 120, 0),
            };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private static string VerdictRu(HealthVerdict v) => v switch
    {
        HealthVerdict.Ok => "Работает",
        HealthVerdict.Stopped => "Остановлен",
        HealthVerdict.Faulty => "Сбой",
        _ => "—",
    };

    private static string ServiceStateRu(ServiceState st) => st switch
    {
        ServiceState.Running => "работает",
        ServiceState.Stopped => "остановлена",
        ServiceState.StartPending => "запускается",
        ServiceState.StopPending => "останавливается",
        ServiceState.NotInstalled => "не установлена",
        _ => "неизвестно",
    };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Крестик прячет окно, а не закрывает приложение — оно живёт в трее.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
