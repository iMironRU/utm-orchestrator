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

        // КОРЕНЬ проблемы «после ребута УТМ на чужих токенах»: службы в Automatic Windows
        // поднимает сам ещё до нашего peel-down, и каждая хватает токен со слота 0 (чужой).
        // Автозапуском УТМ должен владеть ТОЛЬКО оркестратор → переводим управляемые службы
        // в Manual. Идемпотентно (меняем лишь Automatic). После ребута их поднимет этот воркер.
        foreach (var t in targets)
        {
            if (ServiceControl.GetStartMode(t.Service) == StartMode.Automatic
                && ServiceControl.SetStartMode(t.Service, StartMode.Manual, Log))
                Log($"{t.Service}: тип запуска Automatic → Manual (автоподъём УТМ — только оркестратор)");
        }

        // Защита от повторного подъёма: если ВСЕ целевые службы уже Running — считаем,
        // что это не загрузка, а перезапуск службы; подъём не трогаем.
        bool allRunning = targets.All(t => ServiceControl.GetState(t.Service) == ServiceState.Running);
        Log($"все Running: {allRunning}");
        if (allRunning)
        {
            // Второй пояс: даже если все Running, проверим, что каждый на СВОЁМ токене —
            // служба могла подняться на чужом (если что-то стартовало её в обход Manual).
            // Если все на своих — выходим; иначе падаем в introduce-подъём (он остановит и пересадит).
            var misseated = DetectMisseated(targets, Log);
            if (misseated.Count == 0)
            {
                Log("Все УТМ уже Running и на своих токенах — подъём не требуется, выход.");
                return;
            }
            Log($"ВНИМАНИЕ: {misseated.Count} УТМ на ЧУЖИХ токенах [{string.Join(", ", misseated)}] — пересаживаю через introduce-подъём.");
        }
        else
            Log("Обнаружен старт с неподнятыми УТМ — запускаю introduce-подъём.");

        // Синхронный introduce-подъём (PC/SC introduce из конфига, без рестарта
        // SCardSvr) — в отдельном потоке, чтобы не блокировать хост/панель.
        int withReader = targets.Count(t => !string.IsNullOrEmpty(t.ReaderName));
        int eta = BootHistory.MedianSeconds();
        Log($"прогноз подъёма: {(eta > 0 ? eta + "с (по истории)" : "нет истории")}, УТМ к подъёму: {withReader}");

        await Task.Run(() =>
        {
            using var _ = BringUpStatus.Begin(); // панель/трей покажут «Запускается…», не «Сбой»
            BootProgress.Start(withReader, eta);
            var swStart = DateTime.UtcNow;
            try
            {
                var result = BootBringUp.ApplyIntroduce(targets, Log,
                    onProgress: (ready, total, phase, nowStarting, justReady) => BootProgress.Update(phase, nowStarting, justReady));
                Log($"итог: поднято {result.Started.Count}, ошибок {result.Failed.Count}, успех={result.Success}");
                // Записываем длительность только успешного полного подъёма — иначе прогноз поедет.
                if (result.Success)
                    BootHistory.Record((int)(DateTime.UtcNow - swStart).TotalSeconds);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Сбой авто-подъёма УТМ.");
                try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} [svc] СБОЙ: {e}{Environment.NewLine}"); } catch { }
            }
            finally
            {
                BootProgress.Finish();
            }
        }, stoppingToken);
    }

    /// <summary>
    /// Кто из запущенных УТМ сидит на ЧУЖОМ токене. Определённые признаки (не путать с
    /// «нужен перевыпуск RSA»): либо УТМ прочитал ownerId и он НЕ равен ожидаемому ФСРАР,
    /// либо сам УТМ сообщает, что FSRAR_ID сертификата не соответствует его первичной
    /// инициализации. Состояние «нужен перевыпуск RSA» (нет подходящего сертификата,
    /// но токен свой) сюда НЕ попадает — пересаживать его не нужно.
    /// </summary>
    private static List<string> DetectMisseated(List<BootBringUp.Target> targets, Action<string> log)
    {
        var bad = new List<string>();
        try
        {
            using var http = new UtmOrchestrator.Core.Diagnostics.UtmHttpClient(TimeSpan.FromSeconds(4));
            foreach (var t in targets)
            {
                if (t.Port <= 0) continue;
                var info = http.GetInfoAsync(t.Port).GetAwaiter().GetResult();
                if (info is null) continue; // не ответил (ещё грузится) — не трогаем

                bool wrongOwner = !string.IsNullOrEmpty(t.Fsrar) && !string.IsNullOrEmpty(info.OwnerId)
                    && !string.Equals(info.OwnerId, t.Fsrar, StringComparison.OrdinalIgnoreCase);
                bool initMismatch = !string.IsNullOrEmpty(info.RsaError)
                    && (info.RsaError.Contains("первичной инициализации") || info.RsaError.Contains("не соответствует"));
                if (wrongOwner || initMismatch)
                {
                    bad.Add(t.Service);
                    log($"  {t.Service} :{t.Port} на ЧУЖОМ токене (ownerId={info.OwnerId ?? "-"}, ожидался {t.Fsrar ?? "-"}{(initMismatch ? "; УТМ: FSRAR_ID ≠ первичной инициализации" : "")})");
                }
            }
        }
        catch (Exception e) { log($"проверка посадки токенов: {e.Message}"); }
        return bad;
    }
}
