using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

// Крошечный AOT-бутстрапер установки УТМ:Оркестратор.
// Качает из последнего релиза GitHub два архива (app + runtime), распаковывает в одну
// папку, запускает install.ps1 (регистрация службы, SCardSvr, автозапуск трея),
// открывает панель. Дефолтный HttpClient уважает системный прокси (внешние загрузки).
// Строго AOT-безопасно: JsonDocument (DOM, без сериализации), ZipFile, Process.

internal static class Program
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/iMironRU/utm-orchestrator/releases/latest";
    private const string AppPrefix = "UtmOrchestrator-app";
    private const string RuntimePrefix = "UtmOrchestrator-runtime";
    private const string PanelUrl = "http://localhost:8090";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try { Console.Title = "Установка УТМ:Оркестратор"; } catch { }
        bool dry = args.Contains("--dry", StringComparer.OrdinalIgnoreCase);

        Header();
        try
        {
            string staging = await PrepareAsync();
            if (dry)
            {
                Info($"[--dry] распаковано в {staging} — установка пропущена");
                return 0;
            }
            await InstallAsync(staging);
            try { Directory.Delete(staging, recursive: true); } catch { }

            Ok("Готово. Открываю панель управления в браузере…");
            OpenPanel();
            Pause("Установка завершена. Нажмите Enter, чтобы закрыть окно.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Err("Установка не завершена: " + ex.Message);
            Pause("Нажмите Enter, чтобы закрыть окно.");
            return 1;
        }
    }

    // Готовит папку staging: офлайн (app*.zip + runtime*.zip рядом) или скачивает оба.
    private static async Task<string> PrepareAsync()
    {
        string staging = Path.Combine(Path.GetTempPath(), "utmo-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        string baseDir = AppContext.BaseDirectory;
        string? localApp = Directory.EnumerateFiles(baseDir, AppPrefix + "*.zip").FirstOrDefault();
        string? localRt = Directory.EnumerateFiles(baseDir, RuntimePrefix + "*.zip").FirstOrDefault();

        if (localApp is not null && localRt is not null)
        {
            Info($"Локальные архивы: {Path.GetFileName(localRt)} + {Path.GetFileName(localApp)}");
            Extract(localRt, staging);
            Extract(localApp, staging);   // app поверх рантайма
            return staging;
        }

        Info("Ищу последнюю версию на GitHub…");
        var (appUrl, rtUrl) = await ResolveAssetsAsync();

        // Рантайм (большой) — первым; наш код (маленький) — поверх.
        string rtZip = Path.Combine(staging, "_runtime.zip");
        await DownloadAsync(rtUrl, rtZip, "Среда выполнения (один раз)");
        string appZip = Path.Combine(staging, "_app.zip");
        await DownloadAsync(appUrl, appZip, "Приложение");

        Info("Распаковываю…");
        Extract(rtZip, staging);
        Extract(appZip, staging);
        try { File.Delete(rtZip); File.Delete(appZip); } catch { }
        return staging;
    }

    private static async Task InstallAsync(string staging)
    {
        string installPs1 = Directory.EnumerateFiles(staging, "install.ps1", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException("install.ps1 не найден в архиве");

        // PowerShell 5.1 без BOM читает .ps1 как ANSI — кириллица ломает парсинг.
        // Пере-сохраняем с UTF-8 BOM (файл заведомо в UTF-8).
        try
        {
            string ps1 = File.ReadAllText(installPs1);
            File.WriteAllText(installPs1, ps1, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception e) { Info("предупреждение: не удалось нормализовать install.ps1: " + e.Message); }

        Info("Устанавливаю службу и панель…");
        int code = await RunPowerShellAsync(installPs1);
        if (code != 0) throw new Exception($"install.ps1 завершился с кодом {code}");
    }

    private static async Task<(string appUrl, string runtimeUrl)> ResolveAssetsAsync()
    {
        using var http = NewHttp(TimeSpan.FromSeconds(30));
        string json = await http.GetStringAsync(LatestReleaseApi);
        using var doc = JsonDocument.Parse(json);
        string? appUrl = null, rtUrl = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                string url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name.StartsWith(AppPrefix, StringComparison.OrdinalIgnoreCase)) { appUrl = url; Info("  app: " + name); }
                else if (name.StartsWith(RuntimePrefix, StringComparison.OrdinalIgnoreCase)) { rtUrl = url; Info("  runtime: " + name); }
            }
        if (appUrl is null || rtUrl is null)
            throw new Exception("в последнем релизе нет пары app+runtime архивов");
        return (appUrl, rtUrl);
    }

    private static async Task DownloadAsync(string url, string dest, string label)
    {
        using var http = NewHttp(TimeSpan.FromMinutes(10));
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;

        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        int n, lastPct = -1;
        while ((n = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total is > 0)
            {
                int pct = (int)(read * 100 / total.Value);
                if (pct != lastPct) { lastPct = pct; Progress(label, pct, read, total.Value); }
            }
        }
        Console.WriteLine();
    }

    private static async Task<int> RunPowerShellAsync(string scriptPath)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
        };
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, ev) => { if (ev.Data is not null) Console.WriteLine("  " + ev.Data); };
        p.ErrorDataReceived += (_, ev) => { if (ev.Data is not null) Console.WriteLine("  " + ev.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    // UseShellExecute=true открывает URL в браузере по умолчанию.
    private static void OpenPanel()
    {
        try { Process.Start(new ProcessStartInfo(PanelUrl) { UseShellExecute = true }); }
        catch (Exception e) { Info("Не удалось открыть панель: " + e.Message + " (откройте " + PanelUrl + " вручную)"); }
    }

    // Дефолтный клиент уважает системный прокси (нужно для внешних загрузок с GitHub в
    // фильтрованных/корпоративных сетях). UA обязателен для GitHub API.
    private static HttpClient NewHttp(TimeSpan timeout)
    {
        var h = new HttpClient { Timeout = timeout };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("UtmOrchestrator-Setup");
        return h;
    }

    private static void Extract(string zip, string dest) => ZipFile.ExtractToDirectory(zip, dest, overwriteFiles: true);

    // --- вывод ---
    private static void Header()
    {
        Console.WriteLine();
        Console.WriteLine("  УТМ:Оркестратор — установка");
        Console.WriteLine("  Управление несколькими УТМ ЕГАИС на одном компьютере");
        Console.WriteLine("  ──────────────────────────────────────────────────");
        Console.WriteLine();
    }
    private static void Info(string s) => Console.WriteLine("  " + s);
    private static void Ok(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("  " + s); Console.ResetColor(); }
    private static void Err(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  " + s); Console.ResetColor(); }
    private static void Progress(string label, int pct, long read, long total)
    {
        int width = 28, filled = pct * width / 100;
        string bar = new string('#', filled) + new string('.', width - filled);
        Console.Write($"\r  {label}: [{bar}] {pct,3}%  {read / 1_048_576}/{total / 1_048_576} МБ   ");
    }
    private static void Pause(string s)
    {
        Console.WriteLine();
        Console.WriteLine("  " + s);
        try { Console.ReadLine(); } catch { }
    }
}
