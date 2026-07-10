using System.Diagnostics;
using System.Drawing;
using UtmOrchestrator.Core;
using UtmOrchestrator.Core.Health;
using UtmOrchestrator.Core.Services;

namespace UtmOrchestrator.Tray;

/// <summary>
/// Минимальное окно трея: сводный статус (служба + УТМ), кнопка «Открыть
/// расширенную панель в браузере» и действия, которым нужна интерактивная сессия
/// (установка УТМ, лечение токенов), плюс «О программе». Основной интерфейс — веб-
/// панель; здесь только то, что не может делать служба из session 0.
/// Закрытие крестиком прячет окно (приложение живёт в трее).
/// </summary>
public sealed class MainForm : Form
{
    // Адрес веб-панели (совпадает с PanelUrl службы).
    private const string PanelUrl = "http://localhost:8090";

    private readonly Label _svcDot;
    private readonly Label _svcLabel;
    private readonly Label _utmDot;
    private readonly Label _utmLabel;
    private readonly ListView _list;

    public event Action? RefreshRequested;

    public MainForm()
    {
        Text = AppInfo.Title;
        Width = 470;
        Height = 540;
        MinimumSize = new Size(430, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(16),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); // шапка
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // статус службы
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // статус УТМ
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // список УТМ
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96)); // кнопки
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // о программе

        // --- Шапка: название + версия ---
        var header = new Label
        {
            Dock = DockStyle.Fill,
            Text = "УТМ:Оркестратор",
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 40, 46),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        // --- Статус службы ---
        var svcRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _svcDot = MakeDot();
        _svcLabel = new Label { Text = "Служба: —", AutoSize = true, Margin = new Padding(6, 5, 0, 0), Font = new Font("Segoe UI", 9.5F) };
        svcRow.Controls.Add(_svcDot);
        svcRow.Controls.Add(_svcLabel);

        // --- Статус УТМ ---
        var utmRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _utmDot = MakeDot();
        _utmLabel = new Label { Text = "УТМ: —", AutoSize = true, Margin = new Padding(6, 5, 0, 0), Font = new Font("Segoe UI", 9.5F) };
        utmRow.Controls.Add(_utmDot);
        utmRow.Controls.Add(_utmLabel);

        // --- Компактный список УТМ ---
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
        };
        _list.Columns.Add("УТМ", 150);
        _list.Columns.Add("Порт", 55);
        _list.Columns.Add("Состояние", 200);

        // --- Кнопки ---
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var openBtn = new Button
        {
            Text = "Открыть расширенную панель в браузере",
            Dock = DockStyle.Fill,
            Height = 42,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(67, 56, 202),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Margin = new Padding(2, 2, 2, 6),
        };
        openBtn.FlatAppearance.BorderSize = 0;
        openBtn.Click += (_, _) => OpenPanel();
        buttons.Controls.Add(openBtn, 0, 0);
        buttons.SetColumnSpan(openBtn, 2);

        var installBtn = MakeSecondaryButton("Установить УТМ");
        installBtn.Click += (_, _) => MessageBox.Show(this,
            "Мастер установки нового УТМ — следующий этап разработки.\n" +
            "Здесь будет пошаговое добавление УТМ с привязкой токена.",
            "Установить УТМ", MessageBoxButtons.OK, MessageBoxIcon.Information);
        buttons.Controls.Add(installBtn, 0, 1);

        var healBtn = MakeSecondaryButton("Полечить токены");
        healBtn.Click += (_, _) => HealTokens();
        buttons.Controls.Add(healBtn, 1, 1);

        // --- О программе ---
        var about = new LinkLabel
        {
            Dock = DockStyle.Fill,
            Text = $"О программе: {AppInfo.Title}",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(120, 120, 128),
            LinkColor = Color.FromArgb(67, 56, 202),
        };
        about.Links.Add(0, 14, "about"); // «О программе» кликабельно
        about.LinkClicked += (_, _) => ShowAbout();

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(svcRow, 0, 1);
        root.Controls.Add(utmRow, 0, 2);
        root.Controls.Add(_list, 0, 3);
        root.Controls.Add(buttons, 0, 4);
        root.Controls.Add(about, 0, 5);
        Controls.Add(root);
    }

    private static Label MakeDot() => new()
    {
        Text = "●",
        AutoSize = true,
        Font = new Font("Segoe UI", 11F),
        ForeColor = Color.Gray,
        Margin = new Padding(0, 3, 0, 0),
    };

    private static Button MakeSecondaryButton(string text)
    {
        var b = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(40, 40, 46),
            Font = new Font("Segoe UI", 9F),
            Margin = new Padding(2),
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 216);
        b.FlatAppearance.BorderSize = 1;
        return b;
    }

    private void OpenPanel()
    {
        try { Process.Start(new ProcessStartInfo(PanelUrl) { UseShellExecute = true }); }
        catch (Exception e) { MessageBox.Show(this, "Не удалось открыть панель: " + e.Message, Text); }
    }

    private void HealTokens()
    {
        var r = MessageBox.Show(this,
            "«Полечить токены» перезапустит службу смарт-карт и заново поднимет все УТМ " +
            "(обмен на ~несколько минут прервётся). Выполнить сейчас?",
            "Полечить токены", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r == DialogResult.Yes)
            RunElevated("heal", "Лечение токенов (перезапуск смарт-карт + подъём УТМ)");
    }

    // Действия, которым нужна интерактивная сессия + права администратора, запускаем
    // как отдельный процесс CLI с повышением (UAC). CLI лежит рядом с треем.
    private void RunElevated(string cliArgs, string title)
    {
        string cli = Path.Combine(AppContext.BaseDirectory, "UtmOrchestrator.Cli.exe");
        if (!File.Exists(cli))
        {
            MessageBox.Show(this, "Не найден UtmOrchestrator.Cli.exe рядом с приложением.", Text);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(cli, cliArgs) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Exception e)
        {
            // отказ UAC или отсутствие команды
            MessageBox.Show(this, $"{title}: не удалось запустить — {e.Message}", Text);
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            $"{AppInfo.Title}\n\n" +
            "Управление несколькими УТМ ЕГАИС на одной машине.\n" +
            "Основной интерфейс — веб-панель (кнопка выше).\n\n" +
            "github.com/iMironRU/utm-orchestrator",
            "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public void UpdateSnapshot(StatusSnapshot s)
    {
        // Служба оркестратора
        bool svcOk = s.OrchestratorService == ServiceState.Running;
        _svcDot.ForeColor = svcOk ? Color.FromArgb(46, 160, 80) : Color.FromArgb(200, 60, 60);
        _svcLabel.Text = "Служба: " + (s.OrchestratorService == ServiceState.NotInstalled
            ? "не установлена" : ServiceStateRu(s.OrchestratorService));

        // Сводка УТМ
        _utmDot.ForeColor = s.Overall switch
        {
            OverallStatus.Ok => Color.FromArgb(46, 160, 80),
            OverallStatus.Warn => Color.FromArgb(200, 140, 0),
            OverallStatus.Fault => Color.FromArgb(200, 60, 60),
            _ => Color.Gray,
        };
        _utmLabel.Text = "УТМ: " + s.Summary;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var h in s.Utms)
        {
            var item = new ListViewItem(new[]
            {
                h.Instance.ServiceName,
                h.Instance.Port > 0 ? h.Instance.Port.ToString() : "-",
                VerdictRu(h.Verdict) + (string.IsNullOrEmpty(h.Reason) ? "" : " — " + h.Reason),
            });
            item.ForeColor = h.Verdict switch
            {
                HealthVerdict.Ok => Color.FromArgb(46, 130, 70),
                HealthVerdict.Faulty => Color.FromArgb(190, 50, 50),
                HealthVerdict.Stopped => Color.Gray,
                _ => Color.FromArgb(170, 120, 0),
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
