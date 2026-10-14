using System.IO.Compression;

namespace UtmOrchestrator.Core.Diagnostics;

/// <summary>Диагностический архив одного УТМ: логи службы/УТМ, конфиг (без секретов), состояние службы Windows,
/// события, процессы, порты, хвост bringup.log. Используется кнопкой «Диагностика (zip)» и вложением в уведомления.</summary>
public static class DiagBundle
{
    public static byte[] Build(string service, string? folder, int port, string? fsrar, string? serial)
    {
        var inst = new { ServiceName = service, FolderPath = folder, Port = port, ExpectedFsrar = fsrar, TokenSerial = serial };
static byte[]? Tail(string path, int maxBytes)
{
    try
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length, start = Math.Max(0, len - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[len - start];
        int n = 0; while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
        return buf.AsSpan(0, n).ToArray();
    }
    catch { return null; }
}
static string Run(string exe, string args, int timeoutMs = 25000, int codePage = 65001)
{
    try
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
          StandardOutputEncoding = System.Text.Encoding.GetEncoding(codePage), StandardErrorEncoding = System.Text.Encoding.GetEncoding(codePage) };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return "(таймаут)"; }
        return o.Result + e.Result;
    }
    catch (Exception ex) { return "(ошибка запуска: " + ex.Message + ")"; }
}

var ms = new MemoryStream();
using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
{
    void Add(string entry, byte[]? data)
    {
        if (data is null) return;
        var e = zip.CreateEntry(entry, System.IO.Compression.CompressionLevel.Optimal);
        using var s = e.Open(); s.Write(data);
    }
    void AddText(string entry, string text) => Add(entry, new System.Text.UTF8Encoding(true).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(text)).ToArray());

    string root = inst.FolderPath ?? "";
    string logs = Path.Combine(root, "transporter", "l");
    const int MB = 1_048_576;

    AddText("00_summary.txt",
        $"Диагностика УТМ — {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
        $"Машина: {Environment.MachineName}\nОркестратор: {AppInfo.Version}\n" +
        $"Служба: {inst.ServiceName}\nПапка: {root}\nПорт: {inst.Port}\nФСРАР: {inst.ExpectedFsrar}\nСерийник токена: {inst.TokenSerial}\n" +
        $"Папка существует: {Directory.Exists(root)}\n");

    // Логи УТМ/службы (хвосты, чтобы архив был небольшим)
    foreach (var n in new[] { "daemon-error.log", "daemon-output.log", "derby.log" })
        Add("logs/" + n, Tail(Path.Combine(logs, n), 512 * 1024));
    Add("logs/transport_info.log.tail", Tail(Path.Combine(logs, "transport_info.log"), 2 * MB));
    try
    {
        var acc = new DirectoryInfo(logs).EnumerateFiles("access_log.*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (acc != null) Add("logs/" + acc.Name + ".tail", Tail(acc.FullName, 400 * 1024));
        var dts = new DirectoryInfo(logs).EnumerateFiles("daemon-transport.*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(2);
        foreach (var f in dts) Add("logs/" + f.Name, Tail(f.FullName, 256 * 1024));
    }
    catch { }

    // Конфиг без секретов (PIN/пароли маскируем)
    try
    {
        string cfg = Path.Combine(root, "transporter", "conf", "transport.properties");
        if (File.Exists(cfg))
        {
            var lines = File.ReadAllLines(cfg).Select(l =>
                System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*[^#=]*(pin|pass|pwd|secret|key)[^=]*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    ? l.Substring(0, l.IndexOf('=') + 1) + "***" : l);
            AddText("conf/transport.properties", string.Join("\n", lines));
        }
    }
    catch { }

    // Состояние БД Derby: блокировка и каталог
    try
    {
        var db = Path.Combine(root, "transporter", "transportDB");
        var sb = new System.Text.StringBuilder();
        if (Directory.Exists(db))
            foreach (var fi in new DirectoryInfo(db).EnumerateFileSystemInfos())
                sb.AppendLine($"{fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}  {(fi is DirectoryInfo ? "<DIR>" : ((FileInfo)fi).Length.ToString()),10}  {fi.Name}");
        else sb.AppendLine("каталог transportDB не найден");
        AddText("state/transportDB-listing.txt", sb.ToString());
    }
    catch { }

    // Служба Windows: конфиг, состояние, события
    AddText("state/sc-qc.txt", Run("sc.exe", $"qc {inst.ServiceName}", 25000, 866));
    AddText("state/sc-query.txt", Run("sc.exe", $"queryex {inst.ServiceName}", 25000, 866));
    string PS(string script) => Run("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " +
        Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script)));
    string svc = inst.ServiceName.Replace("'", "''");
    AddText("state/eventlog.txt", PS($$"""
        $since=(Get-Date).AddDays(-2)
        '=== System: Service Control Manager по службе {{svc}} ==='
        Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=$since} -ErrorAction SilentlyContinue |
          Where-Object { $_.ProviderName -eq 'Service Control Manager' -and $_.Message -like '*{{svc}}*' } | Select-Object -First 60 |
          ForEach-Object { '{0:yyyy-MM-dd HH:mm:ss} id={1} {2}' -f $_.TimeCreated, $_.Id, ($_.Message -split "`n")[0] }
        '=== Application: java / procrun / Rutoken / PKCS ==='
        Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$since} -ErrorAction SilentlyContinue |
          Where-Object { $_.Message -match 'java|procrun|Rutoken|PKCS|{{svc}}' } | Select-Object -First 60 |
          ForEach-Object { '{0:yyyy-MM-dd HH:mm:ss} [{1}] id={2} {3}' -f $_.TimeCreated, $_.ProviderName, $_.Id, ($_.Message -split "`n")[0] }
        """));
    AddText("state/processes.txt", PS("""
        Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'utm|java|prunsrv' } |
          Select-Object ProcessId,Name,CreationDate,CommandLine | Format-List | Out-String -Width 250
        """));
    AddText("state/port-listeners.txt", Run("netstat.exe", "-ano", 25000, 866));

    // Лог оркестратора (хвост)
    Add("orchestrator/bringup.log.tail", Tail(AppPaths.BringupLog, MB));
}
        ms.Position = 0;
        return ms.ToArray();
    }

    public static string FileName(string service) => $"utm-diag-{service}-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
}
