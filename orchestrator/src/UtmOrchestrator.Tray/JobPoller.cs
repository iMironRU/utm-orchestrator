using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace UtmOrchestrator.Tray;

/// <summary>
/// «Руки» веба: трей в пользовательской (интерактивной) сессии опрашивает службу за
/// интерактивными заданиями и выполняет их там, где служба (session 0) не может:
///  - scan: скан токенов (PKCS11) для мастера установки / обследования;
///  - heal: лечение токенов (рестарт SCardSvr) — с повышением прав (UAC).
/// Скан запускается ОТДЕЛЬНЫМ процессом CLI: если PKCS11 упадёт (AccessViolation),
/// падает только он, а трей выживает. Результат возвращается в службу, веб опрашивает.
/// </summary>
public sealed class JobPoller : IDisposable
{
    private const string Base = "http://localhost:8090";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly System.Windows.Forms.Timer _timer;
    private readonly string _cli = Path.Combine(AppContext.BaseDirectory, "UtmOrchestrator.Cli.exe");
    private bool _busy;

    public JobPoller()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 1500 };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
    }

    private async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            HttpResponseMessage resp;
            try { resp = await _http.GetAsync(Base + "/api/jobs/pending"); }
            catch { return; } // служба недоступна — молча ждём
            if (resp.StatusCode == HttpStatusCode.NoContent || !resp.IsSuccessStatusCode) return;

            PendingJob? job;
            try { job = JsonSerializer.Deserialize<PendingJob>(await resp.Content.ReadAsStringAsync()); }
            catch { return; }
            if (job is null || string.IsNullOrEmpty(job.id)) return;

            string? result = null, error = null;
            try
            {
                switch (job.type)
                {
                    case "scan":
                        // Скан токенов (PKCS11) требует интерактивной сессии — делает трей.
                        // (Лечение/heal теперь делает служба сама, через трей не идёт.)
                        result = RunCli("scan-json", elevated: false, capture: true);
                        break;
                    default:
                        error = "неизвестный тип задания: " + job.type;
                        break;
                }
            }
            catch (Exception e) { error = e.Message; }

            await PostResult(job.id, result, error);
        }
        finally { _busy = false; }
    }

    private string RunCli(string args, bool elevated, bool capture)
    {
        var psi = new ProcessStartInfo(_cli, args);
        if (elevated)
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
        }
        else
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = capture;
        }
        using var p = Process.Start(psi)!;
        if (capture)
        {
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(120000);
            return outp.Trim();
        }
        return "";
    }

    private async Task PostResult(string id, string? result, string? error)
    {
        var body = JsonSerializer.Serialize(new { Result = result, Error = error });
        try { await _http.PostAsync(Base + "/api/jobs/" + id + "/result", new StringContent(body, Encoding.UTF8, "application/json")); }
        catch { /* результат не доставлен — веб получит таймаут */ }
    }

    private sealed record PendingJob(string id, string type, string? prms);

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _http.Dispose();
    }
}
