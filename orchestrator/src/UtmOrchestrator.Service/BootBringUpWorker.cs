using UtmOrchestrator.Core.Recovery;
using UtmOrchestrator.Core.Services;
using UtmOrchestrator.Core.State;

namespace UtmOrchestrator.Service;

/// <summary>
/// На старте службы (== на загрузке Windows, т.к. служба auto-start) один раз
/// поднимает все УТМ методом INTRODUCE (способ 2UTM: PC/SC introduce из конфига,
/// БЕЗ рестарта SCardSvr и БЕЗ живого PKCS11-скана). Проверено: работает из
/// session 0 (LocalSystem). Требует тёплого SCardSvr (StartType=Automatic) и
/// заполненного ReaderName в state.json. Идемпотентно: если УТМ уже Running —
/// ничего не делает. В фоне, чтобы не задерживать старт панели.
/// Отключается конфигом BringUpOnStart=false.
/// </summary>
public sealed class BootBringUpWorker : BackgroundService
{
    private readonly ILogger<BootBringUpWorker> _log;
    private readonly bool _enabled;

    public BootBringUpWorker(ILogger<BootBringUpWorker> log, IConfiguration config)
    {
        _log = log;
        _enabled = config.GetValue("BringUpOnStart", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Безусловная диагностика в файл (виден всегда, в отличие от EventLog).
        string logPath = UtmOrchestrator.Core.AppPaths.BringupLog;
        void Log(string m)
        {
            _log.LogInformation("[bringup] {Msg}", m);
            try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} [svc] {m}{Environment.NewLine}"); } catch { }
        }
        try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); } catch { }

        string user = Environment.UserName;
        bool interactive = Environment.UserInteractive;
        Log($"worker старт: enabled={_enabled}, user={user}, interactive={interactive}, baseDir={AppContext.BaseDirectory}");

        if (!_enabled)
        {
            Log("BringUpOnStart=false — авто-подъём отключён, выход.");
            return;
        }

        // Задержка: на реальной загрузке драйверу токенов/USB нужно время появиться.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
        var targets = state.Instances
            .Where(i => !string.IsNullOrEmpty(i.TokenSerial))
            .Select(i => new BootBringUp.Target(i.ServiceName, i.Port, i.TokenSerial!, i.ExpectedFsrar, i.ReaderName))
            .ToList();
        int noReader = targets.Count(t => string.IsNullOrEmpty(t.ReaderName));
        Log($"привязок загружено: {targets.Count} (без ReaderName: {noReader})");

        if (targets.Count == 0)
        {
            Log($"Нет привязок в {OrchestratorState.DefaultPath} — выход.");
            return;
        }

        // Защита от повторного подъёма: если ВСЕ целевые службы уже Running — считаем,
        // что это не загрузка, а перезапуск службы; подъём не трогаем.
        bool allRunning = targets.All(t => ServiceControl.GetState(t.Service) == ServiceState.Running);
        Log($"все Running: {allRunning}");
        if (allRunning)
        {
            Log("Все УТМ уже Running — подъём не требуется, выход.");
            return;
        }

        Log("Обнаружен старт с неподнятыми УТМ — запускаю introduce-подъём.");

        // Синхронный introduce-подъём (PC/SC introduce из конфига, без рестарта
        // SCardSvr) — в отдельном потоке, чтобы не блокировать хост/панель.
        await Task.Run(() =>
        {
            try
            {
                var result = BootBringUp.ApplyIntroduce(targets, Log);
                Log($"итог: поднято {result.Started.Count}, ошибок {result.Failed.Count}, успех={result.Success}");
            }
            catch (Exception e)
            {
                _log.LogError(e, "Сбой авто-подъёма УТМ.");
                try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} [svc] СБОЙ: {e}{Environment.NewLine}"); } catch { }
            }
        }, stoppingToken);
    }
}
