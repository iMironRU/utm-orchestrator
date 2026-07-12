using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace UtmOrchestrator.Setup;

/// <summary>
/// Дружелюбный установщик: скачивает из последнего релиза GitHub два архива —
/// app (наш код) и runtime (общий .NET) — (или берёт лежащие рядом app*.zip +
/// runtime*.zip для офлайна), распаковывает оба в одну папку, запускает install.ps1
/// (регистрация службы, SCardSvr, автозапуск трея), затем открывает веб-панель на
/// первый запуск (обследование + подхват существующих УТМ). Права администратора
/// (app.manifest).
/// </summary>
public sealed class SetupForm : Form
{
    // Продукт теперь в ДВУХ артефактах: app (наш код) + runtime (общий .NET). Ставим оба
    // в одну папку — получается self-contained-установка. Имена версионные, поэтому URL
    // берём из последнего релиза через GitHub API.
    private const string LatestReleaseApi =
        "https://api.github.com/repos/iMironRU/utm-orchestrator/releases/latest";
    private const string AppPrefix = "UtmOrchestrator-app";
    private const string RuntimePrefix = "UtmOrchestrator-runtime";
    private const string PanelUrl = "http://localhost:8090";

    private readonly Button _btn;
    private readonly Label _status;
    private readonly ProgressBar _bar;
    private readonly TextBox _log;
    private bool _done;

    public SetupForm()
    {
        Text = "Установка УТМ:Оркестратор";
        Width = 560;
        Height = 460;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.5F);

        var header = new Label
        {
            Text = "УТМ:Оркестратор",
            Font = new Font("Segoe UI", 18F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 40, 46),
            AutoSize = false,
            Location = new Point(24, 20),
            Size = new Size(510, 34),
        };
        var subtitle = new Label
        {
            Text = "Управление несколькими УТМ ЕГАИС на одном компьютере",
            ForeColor = Color.FromArgb(110, 110, 120),
            AutoSize = false,
            Location = new Point(24, 56),
            Size = new Size(510, 22),
        };
        var desc = new Label
        {
            Text = "Установщик скачает последнюю версию, установит службу и панель " +
                   "управления, настроит автозапуск и откроет панель в браузере. " +
                   "Уже установленные УТМ будут найдены и подхвачены.",
            AutoSize = false,
            Location = new Point(24, 86),
            Size = new Size(510, 56),
            ForeColor = Color.FromArgb(60, 60, 68),
        };

        _btn = new Button
        {
            Text = "Установить",
            Location = new Point(24, 150),
            Size = new Size(160, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(67, 56, 202),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
        };
        _btn.FlatAppearance.BorderSize = 0;
        _btn.Click += OnButton;

        _status = new Label
        {
            Text = "Готово к установке.",
            AutoSize = false,
            Location = new Point(24, 200),
            Size = new Size(510, 20),
            ForeColor = Color.FromArgb(60, 60, 68),
        };
        _bar = new ProgressBar
        {
            Location = new Point(24, 224),
            Size = new Size(510, 14),
            Style = ProgressBarStyle.Continuous,
        };
        _log = new TextBox
        {
            Location = new Point(24, 248),
            Size = new Size(510, 150),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(248, 248, 246),
            Font = new Font("Consolas", 8.5F),
        };

        Controls.AddRange(new Control[] { header, subtitle, desc, _btn, _status, _bar, _log });
    }

    private async void OnButton(object? sender, EventArgs e)
    {
        if (_done) { OpenPanel(); return; }

        _btn.Enabled = false;
        try
        {
            await InstallAsync();
            SetStatus("Готово. Открываю панель управления…");
            _done = true;
            _btn.Text = "Открыть панель";
            _btn.Enabled = true;
            OpenPanel();
        }
        catch (Exception ex)
        {
            Log("ОШИБКА: " + ex.Message);
            SetStatus("Установка не завершена — см. журнал ниже.");
            _btn.Text = "Повторить";
            _btn.Enabled = true;
        }
    }

    private async Task InstallAsync()
    {
        // 1. Найти архивы рядом (офлайн: app*.zip + runtime*.zip) или скачать оба из
        //    последнего релиза (URL берём через GitHub API — имена версионные).
        string staging = Path.Combine(Path.GetTempPath(), "utmo-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        string? localApp = Directory.EnumerateFiles(AppContext.BaseDirectory, AppPrefix + "*.zip").FirstOrDefault();
        string? localRt = Directory.EnumerateFiles(AppContext.BaseDirectory, RuntimePrefix + "*.zip").FirstOrDefault();
        if (localApp is not null && localRt is not null)
        {
            Log($"Использую локальные архивы: {Path.GetFileName(localApp)} + {Path.GetFileName(localRt)}");
            SetStatus("Распаковываю…");
            await ExtractInto(localRt, staging);
            await ExtractInto(localApp, staging);   // app поверх рантайма
        }
        else
        {
            SetStatus("Ищу последнюю версию на GitHub…");
            var (appUrl, rtUrl) = await ResolveAssetsAsync();
            // Рантайм (большой) — первым; наш код (маленький) — поверх.
            SetStatus("Скачиваю среду выполнения (~65 МБ, один раз)…");
            string rtZip = Path.Combine(staging, "_runtime.zip");
            await DownloadAsync(rtUrl, rtZip);
            SetStatus("Скачиваю приложение…");
            string appZip = Path.Combine(staging, "_app.zip");
            await DownloadAsync(appUrl, appZip);

            SetStatus("Распаковываю…");
            _bar.Style = ProgressBarStyle.Marquee;
            await ExtractInto(rtZip, staging);
            await ExtractInto(appZip, staging);
            try { File.Delete(rtZip); File.Delete(appZip); } catch { }
        }
        Log("Подготовлено в " + staging);

        // 3. Найти install.ps1 (лежит в корне архива).
        string installPs1 = Directory.EnumerateFiles(staging, "install.ps1", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException("install.ps1 не найден в архиве");

        // 4. Пере-сохранить install.ps1 с BOM: PowerShell 5.1 без BOM читает .ps1 как
        //    ANSI, и кириллица в строках ломает парсинг. Мы знаем, что файл в UTF-8.
        try
        {
            string ps1 = File.ReadAllText(installPs1); // авто-детект BOM, иначе UTF-8
            File.WriteAllText(installPs1, ps1, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception e) { Log("предупреждение: не удалось нормализовать install.ps1: " + e.Message); }

        // Запустить install.ps1 (мы уже с правами администратора — powershell наследует).
        SetStatus("Устанавливаю службу и панель…");
        int code = await RunPowerShellAsync(installPs1);
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = 100;
        if (code != 0) throw new Exception($"install.ps1 завершился с кодом {code}");

        // 5. Прибраться (не критично).
        try { Directory.Delete(staging, recursive: true); } catch { }
    }

    private static Task ExtractInto(string zip, string dest) =>
        Task.Run(() => ZipFile.ExtractToDirectory(zip, dest, overwriteFiles: true));

    // Находит URL обоих артефактов (app + runtime) в последнем релизе через GitHub API.
    private async Task<(string appUrl, string runtimeUrl)> ResolveAssetsAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UtmOrchestrator-Setup"); // GitHub API требует UA
        string json = await http.GetStringAsync(LatestReleaseApi);
        using var doc = JsonDocument.Parse(json);
        string? appUrl = null, rtUrl = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith(AppPrefix, StringComparison.OrdinalIgnoreCase)) { appUrl = a.GetProperty("browser_download_url").GetString(); Log("app: " + name); }
                else if (name.StartsWith(RuntimePrefix, StringComparison.OrdinalIgnoreCase)) { rtUrl = a.GetProperty("browser_download_url").GetString(); Log("runtime: " + name); }
            }
        if (appUrl is null || rtUrl is null)
            throw new Exception("в последнем релизе нет пары app+runtime архивов");
        return (appUrl, rtUrl);
    }

    private async Task DownloadAsync(string url, string dest)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UtmOrchestrator-Setup");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        _bar.Style = ProgressBarStyle.Continuous;

        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total is > 0)
            {
                int pct = (int)(read * 100 / total.Value);
                SetProgress(pct);
                SetStatus($"Скачиваю последнюю версию… {read / 1_048_576} / {total.Value / 1_048_576} МБ");
            }
        }
        Log($"Скачано: {read / 1_048_576} МБ → {dest}");
    }

    private async Task<int> RunPowerShellAsync(string scriptPath)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // install.ps1 переключает вывод в UTF-8 — читаем так же, иначе кириллица
            // в логе превращается в кракозябры.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
        };
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, ev) => { if (ev.Data != null) Log(ev.Data); };
        p.ErrorDataReceived += (_, ev) => { if (ev.Data != null) Log(ev.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    private void OpenPanel()
    {
        try { Process.Start(new ProcessStartInfo(PanelUrl) { UseShellExecute = true }); }
        catch (Exception e) { Log("Не удалось открыть панель: " + e.Message); }
    }

    // --- потокобезопасные обновления UI ---
    private void SetStatus(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(s)); return; }
        _status.Text = s;
    }
    private void SetProgress(int pct)
    {
        if (InvokeRequired) { BeginInvoke(() => SetProgress(pct)); return; }
        _bar.Value = Math.Clamp(pct, 0, 100);
    }
    private void Log(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(line)); return; }
        _log.AppendText(line + Environment.NewLine);
    }
}
