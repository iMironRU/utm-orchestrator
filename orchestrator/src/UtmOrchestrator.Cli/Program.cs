using System.Text;
using UtmOrchestrator.Core.Diagnostics;
using UtmOrchestrator.Core.Discovery;
using UtmOrchestrator.Core.Readers;
using UtmOrchestrator.Core.Recovery;
using UtmOrchestrator.Core.Services;
using UtmOrchestrator.Core.State;
using UtmOrchestrator.Core.Tokens;

Console.OutputEncoding = Encoding.UTF8;

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "scan";

switch (command)
{
    case "scan":
        ScanTokens();
        break;
    case "readers":
        ListReaders();
        break;
    case "status":
        await ShowStatus();
        break;
    case "discover":
        await Discover();
        break;
    case "savebindings":
        await SaveBindings();
        break;
    case "bringup":
        BringUp(args.Length > 1 ? args[1].ToLowerInvariant() : "verify");
        break;
    case "restart":
        RestartUtm(args.Length > 1 ? args[1] : "");
        break;
    case "heal":
        Heal();
        break;
    case "slotcount":
        SlotCount();
        break;
    case "pcsctest":
        PcscTest();
        break;
    default:
        Console.WriteLine($"Неизвестная команда: {command}");
        Console.WriteLine("Доступно: scan, readers, status, discover, savebindings, bringup <verify|apply>");
        break;
}

// Собрать привязки (служба↔порт↔ФСРАР↔серийник) из живого discovery и сохранить
// в state.json — источник истины для boot bring-up. Запускать, пока УТМ работают.
static async Task SaveBindings()
{
    var instances = await UtmDiscovery.DiscoverAsync();
    var state = new OrchestratorState { Instances = instances.ToList() };
    state.Save(OrchestratorState.DefaultPath);
    Console.WriteLine($"Сохранено привязок: {instances.Count} → {OrchestratorState.DefaultPath}");
    foreach (var i in instances)
        Console.WriteLine($"  {i.ServiceName,-11} порт={i.Port} fsrar={i.ExpectedFsrar ?? "-"} serial={i.TokenSerial ?? "-"}");
    int noSerial = instances.Count(i => string.IsNullOrEmpty(i.TokenSerial));
    if (noSerial > 0)
        Console.WriteLine($"ВНИМАНИЕ: у {noSerial} УТМ нет серийника (кэш serials.json пуст?) — bringup их не поднимет.");
}

// Boot bring-up (peel-down):
//   verify — только план (безопасно, без действий);
//   apply  — интерактивно (запрос yes), реально поднимает УТМ;
//   boot   — для задачи «при входе»: без запроса, с защитой «все Running → пропуск».
// apply/boot СТОПАЮТ все УТМ и сбрасывают ридеры — только в окне обслуживания/на входе.
static void BringUp(string mode)
{
    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    if (state.Instances.Count == 0)
    {
        Console.WriteLine($"Нет привязок в {OrchestratorState.DefaultPath}. Сначала: cli savebindings");
        return;
    }
    var targets = state.Instances
        .Where(i => !string.IsNullOrEmpty(i.TokenSerial))
        .Select(i => new BootBringUp.Target(i.ServiceName, i.Port, i.TokenSerial!, i.ExpectedFsrar, i.ReaderName))
        .ToList();

    // introduce-путь (способ 2UTM, без рестарта SCardSvr) — из конфига ReaderName.
    //   introduce-verify — план; introduce — интерактивно (yes); introduce-boot —
    //   без запроса + идемпотентность (для службы/задачи).
    if (mode == "introduce" || mode == "introduce-verify" || mode == "introduce-boot")
    {
        bool idry = mode == "introduce-verify";
        string ilogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "UtmOrchestrator", "bringup.log");
        Directory.CreateDirectory(Path.GetDirectoryName(ilogPath)!);
        void ILog(string m)
        {
            string line = $"{DateTime.Now:HH:mm:ss} {m}";
            Console.WriteLine(line);
            try { File.AppendAllText(ilogPath, line + Environment.NewLine); } catch { }
        }
        if (mode == "introduce-boot")
        {
            ILog("=== introduce-boot запущен ===");
            if (targets.All(t => ServiceControl.GetState(t.Service) == ServiceState.Running))
            {
                ILog("introduce-boot: все УТМ уже Running — подъём не требуется.");
                return;
            }
        }
        else if (!idry)
        {
            Console.WriteLine("!!! introduce apply: остановит все УТМ и переставит ридеры (без рестарта SCardSvr). Окно обслуживания.");
            Console.WriteLine("Продолжить? Введите  yes :");
            if ((Console.ReadLine() ?? "").Trim().ToLowerInvariant() != "yes") { Console.WriteLine("Отменено."); return; }
        }
        try
        {
            var ir = BootBringUp.ApplyIntroduce(targets, ILog, dryRun: idry);
            if (!idry) { PersistReaders(state, ir.ReaderBySerial, ILog); ILog($"introduce: поднято {ir.Started.Count}, ошибок {ir.Failed.Count}, успех={ir.Success}"); }
        }
        catch (Exception e) { ILog($"introduce: СБОЙ — {e}"); }
        return;
    }

    if (mode == "verify")
    {
        BootBringUp.Apply(targets, Console.WriteLine, dryRun: true);
        Console.WriteLine("verify: план показан, действий не выполнено.");
        return;
    }

    // Лог и в консоль, и в файл — вывод задачи/службы иначе не увидеть.
    string logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UtmOrchestrator", "bringup.log");
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    void Log(string m)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {m}";
        Console.WriteLine(line);
        try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
    }

    if (mode == "boot")
    {
        Log($"=== bringup boot запущен ({mode}) ===");
        // Идемпотентность: если все УТМ уже подняты — это не «после загрузки», пропускаем.
        if (targets.All(t => ServiceControl.GetState(t.Service) == ServiceState.Running))
        {
            Log("boot: все УТМ уже Running — подъём не требуется.");
            return;
        }
        try
        {
            var r = BootBringUp.Apply(targets, Log, dryRun: false);
            PersistReaders(state, r.ReaderBySerial, Log);
            Log($"boot: поднято {r.Started.Count}, ошибок {r.Failed.Count}, успех={r.Success}");
        }
        catch (Exception e) { Log($"boot: СБОЙ — {e}"); }
        return;
    }

    if (mode == "apply")
    {
        Console.WriteLine("!!! apply: будут ОСТАНОВЛЕНЫ все УТМ и сброшены ридеры. Только в окне обслуживания.");
        Console.WriteLine("Продолжить? Введите  yes  для подтверждения:");
        if ((Console.ReadLine() ?? "").Trim().ToLowerInvariant() != "yes")
        {
            Console.WriteLine("Отменено.");
            return;
        }
        try
        {
            var r = BootBringUp.Apply(targets, Log, dryRun: false);
            PersistReaders(state, r.ReaderBySerial, Log);
            Log($"Итог: поднято {r.Started.Count}, ошибок {r.Failed.Count}, успех={r.Success}");
        }
        catch (Exception e) { Log($"apply: СБОЙ — {e}"); }
        return;
    }

    Console.WriteLine($"Неизвестный режим bringup: {mode}. Доступно: verify | apply | boot");
}

// Записать наблюдённые ридеры (серийник→ридер) в state.json — «конфиг» для introduce.
static void PersistReaders(OrchestratorState state, IReadOnlyDictionary<string, string> readerBySerial, Action<string> log)
{
    if (readerBySerial.Count == 0) return;
    int upd = 0;
    foreach (var inst in state.Instances)
    {
        if (!string.IsNullOrEmpty(inst.TokenSerial)
            && readerBySerial.TryGetValue(inst.TokenSerial, out var reader)
            && inst.ReaderName != reader)
        {
            inst.ReaderName = reader;
            upd++;
        }
    }
    if (upd > 0)
    {
        state.Save(OrchestratorState.DefaultPath);
        log($"обновлено ридеров в state.json: {upd}");
    }
}

static async Task Discover()
{
    var instances = await UtmDiscovery.DiscoverAsync();
    Console.WriteLine($"Найдено УТМ (служб Transport*): {instances.Count}");
    foreach (var i in instances)
    {
        Console.WriteLine(
            $"  {i.ServiceName,-11} порт={i.Port,-5} fsrar={i.ExpectedFsrar ?? "-",-14} serial={i.TokenSerial ?? "-",-10} папка={i.FolderPath}");
    }
}

// Стандартная раскладка 2UTM: 8080=Transport(base), 8081=Transport2, ... 8085=Transport6.
static (int Port, string Service)[] StandardLayout() => new[]
{
    (8080, "Transport"),
    (8081, "Transport2"),
    (8082, "Transport3"),
    (8083, "Transport4"),
    (8084, "Transport5"),
    (8085, "Transport6"),
};

static async Task ShowStatus()
{
    using var http = new UtmHttpClient();
    Console.WriteLine("порт  служба       состояние    ownerId         rsa      gost");
    foreach (var (port, service) in StandardLayout())
    {
        var state = ServiceControl.GetState(service);
        var info = await http.GetInfoAsync(port);
        string own = info?.OwnerId ?? "-";
        string rsa = info == null ? "нет ответа" : (info.RsaOk ? "ok" : "ОШИБКА");
        string gost = info?.GostValid == true ? "ok" : "-";
        Console.WriteLine($"{port}  {service,-11}  {state,-11}  {own,-14}  {rsa,-8}  {gost}");
    }
}

static void ListReaders()
{
    using var table = new ReaderTable();
    var readers = table.ListReaders();
    Console.WriteLine($"PC/SC ридеров: {readers.Count}");
    foreach (var r in readers)
    {
        string device;
        try { device = table.GetDeviceSystemName(r); }
        catch (Exception e) { device = $"<ошибка: {e.Message}>"; }
        Console.WriteLine($"  '{r}'  -> устройство '{device}'");
    }
}

// Диагностика доступа к PKCS11: счёт слотов (безопасно). Пишет и в файл — чтобы
// увидеть результат при запуске из планировщика (от SYSTEM), где консоль не видна.
static void SlotCount()
{
    string who = $"user={Environment.UserName}, interactive={Environment.UserInteractive}";
    string outPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UtmOrchestrator", "slotcount.txt");
    string line;
    try
    {
        int n = new TokenScanner().CountTokens();
        line = $"{DateTime.Now:HH:mm:ss} slots={n} ({who})";
    }
    catch (Exception e)
    {
        line = $"{DateTime.Now:HH:mm:ss} ОШИБКА: {e.GetType().Name}: {e.Message} ({who})";
    }
    Console.WriteLine(line);
    try { Directory.CreateDirectory(Path.GetDirectoryName(outPath)!); File.AppendAllText(outPath, line + Environment.NewLine); } catch { }
}

// Проверка PC/SC write-операций (introduce/forget) из текущего контекста БЕЗ
// рестарта SCardSvr и БЕЗ касания реальных ридеров (фиктивный алиас). Это тот путь,
// которым служба (session 0) будет двигать ридеры «по способу 2UTM». Пишет в файл —
// чтобы увидеть результат при запуске от SYSTEM (консоль не видна).
static void PcscTest()
{
    string outPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UtmOrchestrator", "pcsctest.txt");
    var sb = new StringBuilder();
    void L(string m) { Console.WriteLine(m); sb.AppendLine($"{DateTime.Now:HH:mm:ss} {m}"); }

    string who = $"user={Environment.UserName}, interactive={Environment.UserInteractive}";
    L($"=== pcsctest ({who}) ===");
    const string probe = "UTMORCH_PROBE";
    const string fakeDevice = "UTMORCH_FAKE_DEVICE"; // несуществующее устройство — реальные токены не трогаем
    try
    {
        using var t = new ReaderTable();
        var before = t.ListReaders();
        L($"контекст установлен OK; ридеров сейчас: {before.Count}");

        int rvI = t.IntroduceReader(probe, fakeDevice);
        L($"IntroduceReader('{probe}') rv=0x{rvI:X8}");

        bool appeared = t.ListReaders().Contains(probe);
        L($"алиас появился в списке: {appeared}");

        int rvF = t.ForgetReader(probe);
        L($"ForgetReader('{probe}') rv=0x{rvF:X8}");

        bool ok = rvI == 0 && rvF == 0;
        L($"ВЕРДИКТ: PC/SC introduce/forget из этого контекста = {(ok ? "РАБОТАЕТ" : "НЕ работает")}");
    }
    catch (Exception e)
    {
        L($"ОШИБКА: {e.GetType().Name}: {e.Message}");
    }
    try { Directory.CreateDirectory(Path.GetDirectoryName(outPath)!); File.AppendAllText(outPath, sb.ToString()); } catch { }
}

// «Полечить токены»: сброс SCardSvr (будит замёрзшие/уснувшие токены) + introduce-
// подъём всех УТМ. Для аварийного случая «токен завис». Запускается из трея с UAC —
// держим консоль открытой до Enter, чтобы оператор увидел результат.
static void Heal()
{
    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var targets = state.Instances
        .Where(i => !string.IsNullOrEmpty(i.TokenSerial))
        .Select(i => new BootBringUp.Target(i.ServiceName, i.Port, i.TokenSerial!, i.ExpectedFsrar, i.ReaderName))
        .ToList();
    string logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UtmOrchestrator", "bringup.log");
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    void Log(string m)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {m}";
        Console.WriteLine(line);
        try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
    }

    Log("=== heal (лечение токенов) запущен ===");
    if (targets.Count == 0) { Log("Нет привязок в state.json — нечего лечить."); }
    else
    {
        try
        {
            // 1) Сброс к нативному (рестарт SCardSvr будит замёрзшие токены).
            ReaderReset.ResetToNative(targets.Select(t => t.Service), Log);
            // 2) Подъём всех УТМ через introduce (оставит ридеры введёнными).
            var r = BootBringUp.ApplyIntroduce(targets, Log);
            PersistReaders(state, r.ReaderBySerial, Log);
            Log($"heal: поднято {r.Started.Count}, ошибок {r.Failed.Count}, успех={r.Success}");
        }
        catch (Exception e) { Log($"heal: СБОЙ — {e}"); }
    }
    Console.WriteLine();
    Console.WriteLine("Готово. Нажмите Enter, чтобы закрыть окно.");
    try { Console.ReadLine(); } catch { }
}

// Перезапуск одного УТМ через introduce (не трогая остальные). Пишет в bringup.log.
static void RestartUtm(string service)
{
    if (string.IsNullOrWhiteSpace(service)) { Console.WriteLine("Укажите службу: restart <ServiceName>"); return; }
    var state = OrchestratorState.Load(OrchestratorState.DefaultPath);
    var inst = state.Instances.FirstOrDefault(i => string.Equals(i.ServiceName, service, StringComparison.OrdinalIgnoreCase));
    if (inst is null) { Console.WriteLine($"Служба {service} не найдена в {OrchestratorState.DefaultPath}"); return; }

    string logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UtmOrchestrator", "bringup.log");
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    void Log(string m)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {m}";
        Console.WriteLine(line);
        try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
    }

    var t = new BootBringUp.Target(inst.ServiceName, inst.Port, inst.TokenSerial ?? "", inst.ExpectedFsrar, inst.ReaderName);
    var allReaders = state.Instances.Select(i => i.ReaderName ?? "").Where(r => r.Length > 0).ToList();
    try { bool ok = BootBringUp.RestartOne(t, allReaders, Log); Log($"restart {service}: {(ok ? "успех" : "ОШИБКА")}"); }
    catch (Exception e) { Log($"restart {service}: СБОЙ — {e}"); }
}

static void ScanTokens()
{
    var scanner = new TokenScanner();
    var tokens = scanner.Scan();
    Console.WriteLine($"Найдено токенов: {tokens.Count}");
    foreach (var t in tokens)
    {
        Console.WriteLine(
            $"  slot {t.SlotId}: serial={t.Serial}  fsrar={(t.HasFsrar ? t.FsrarId : "<нет>")}  reader='{t.ReaderName}'");
    }
}
