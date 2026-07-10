using System.Runtime.Versioning;
using UtmOrchestrator.Core.Diagnostics;
using UtmOrchestrator.Core.Readers;
using UtmOrchestrator.Core.Services;
using UtmOrchestrator.Core.Tokens;

namespace UtmOrchestrator.Core.Recovery;

/// <summary>
/// Подъём всех УТМ при загрузке методом «peel-down» (только forget, без
/// переименований — проверено надёжнее introduce-подхода, см. NOTES §11/§6).
///
/// Почему так (см. NOTES §6.17): каждый Transport при первом обращении к PKCS11
/// слепо берёт слот 0. Значит нужно, чтобы в момент старта именно ЕГО токен был
/// слотом 0. Алгоритм:
///   1. Сброс PC/SC к нативному состоянию (все Transport стоп, forget всех
///      ридеров, рестарт SCardSvr) → токены перечисляются в порядке USB-портов.
///   2. ОДИН скан PKCS11 (все токены свободны — службы стоят) → фиксируем порядок
///      слотов [t0..tn] с серийниками.
///   3. Для i = 0..n: сейчас слот 0 = t_i. Старт службы, чей ожидаемый серийник =
///      t_i.Serial, ждём готовности по HTTP, затем forget ридер слота 0 → слотом 0
///      становится t_{i+1}.
///
/// КРИТИЧНО: повторных сканов НЕ делаем. C_GetTokenInfo по токену, занятому уже
/// поднятым УТМ, роняет процесс (AccessViolation, в .NET не ловится). Один скан на
/// свободных токенах — безопасен.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BootBringUp
{
    /// <summary>Что должно подняться: служба, порт (для проверки), ожидаемый серийник токена,
    /// и (для introduce-пути) нативное имя ридера токена из конфига.</summary>
    public sealed record Target(string Service, int Port, string ExpectedSerial, string? Fsrar, string? ReaderName = null);

    public sealed record Result(
        bool Success,
        IReadOnlyList<string> Started,
        IReadOnlyList<string> Failed,
        IReadOnlyDictionary<string, string> ReaderBySerial);

    /// <summary>
    /// Выполнить подъём. <paramref name="dryRun"/> = только печать плана, без действий.
    /// Требует прав администратора (start/stop служб, рестарт SCardSvr).
    /// </summary>
    public static Result Apply(IReadOnlyList<Target> targets, Action<string> log, bool dryRun = false)
    {
        var started = new List<string>();
        var failed = new List<string>();

        var services = targets.Select(t => t.Service).ToList();
        var bySerial = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
            if (!string.IsNullOrEmpty(t.ExpectedSerial)) bySerial[t.ExpectedSerial] = t;

        // --- Режим проверки: НИЧЕГО не трогаем и НЕ сканируем PKCS11 (УТМ могут
        //     работать; скан занятых токенов роняет процесс). Только печатаем план. ---
        if (dryRun)
        {
            log("[verify] Привязки (серийник → служба:порт):");
            foreach (var t in targets)
                log($"  {t.ExpectedSerial} → {t.Service} :{t.Port} (fsrar {t.Fsrar ?? "-"})");
            try
            {
                using var rt = new ReaderTable();
                var rs = rt.ListReaders();
                log($"[verify] Сейчас PC/SC ридеров: {rs.Count}: {string.Join(", ", rs)}");
            }
            catch (Exception e) { log($"[verify] PC/SC недоступен: {e.Message}"); }
            log("[verify] Apply выполнит: reset→native, один скан, затем по слотам "
                + "старт службы + forget слота 0. Реальных действий не сделано.");
            return new Result(true, started, failed, new Dictionary<string, string>());
        }

        // 1. Сброс к нативному состоянию.
        ReaderReset.ResetToNative(services, log);

        // 2. Дождаться появления ВСЕХ токенов и один раз зафиксировать порядок слотов.
        //    Службы стоят → скан безопасен. После рестарта SCardSvr токены
        //    перечисляются не мгновенно (бывало «6 устройств, 4 карты») — ждём.
        int expected = bySerial.Count;
        IReadOnlyList<TokenInfo> tokens = Array.Empty<TokenInfo>();
        var scanDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try { tokens = new TokenScanner().Scan(); }
            catch (Exception e) { log($"скан токенов: {e.Message}"); tokens = Array.Empty<TokenInfo>(); }

            if (tokens.Count >= expected) break;
            if (DateTime.UtcNow >= scanDeadline)
            {
                log($"ВНИМАНИЕ: вижу {tokens.Count} из {expected} токенов — продолжаю с тем, что есть.");
                break;
            }
            log($"токенов пока {tokens.Count}/{expected}, жду переустановки…");
            Thread.Sleep(2000);
        }

        log($"Токенов после сброса: {tokens.Count} (порядок слотов = порядок подъёма)");
        // Наблюдённое соответствие серийник → нативный ридер (аналог attrReader 2UTM).
        // Захватываем при каждом подъёме — держим «конфиг» ридеров в актуальном виде.
        var readerBySerial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!string.IsNullOrEmpty(tokens[i].Serial) && !string.IsNullOrEmpty(tokens[i].ReaderName))
                readerBySerial[tokens[i].Serial] = tokens[i].ReaderName;
            var m = bySerial.TryGetValue(tokens[i].Serial, out var tg) ? $"{tg.Service} :{tg.Port}" : "нет привязки";
            log($"  слот {i}: serial={tokens[i].Serial} reader='{tokens[i].ReaderName}' → {m}");
        }

        // 3. Peel-down: для каждого слота по порядку — старт службы, затем forget слота 0.
        using var readers = new ReaderTable();
        using var http = new UtmHttpClient(TimeSpan.FromSeconds(3));

        for (int i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];

            // Текущий ридер слота 0 в PC/SC (после предыдущих forget он «съезжает»).
            var pcsc = readers.ListReaders();
            string reader0 = pcsc.Count > 0 ? pcsc[0] : tok.ReaderName;

            if (bySerial.TryGetValue(tok.Serial, out var target))
            {
                log($"→ Слот 0 = {tok.Serial} → поднимаю {target.Service} (порт {target.Port})");
                if (!dryRun)
                {
                    ServiceControl.Stop(target.Service, TimeSpan.FromSeconds(60));
                    Thread.Sleep(500);
                    bool run = ServiceControl.Start(target.Service, TimeSpan.FromSeconds(90));
                    bool up = run && WaitHttp(http, target.Port, TimeSpan.FromSeconds(90), target.Fsrar, log);
                    if (up) { started.Add(target.Service); log($"  ✓ {target.Service} готов"); }
                    else { failed.Add(target.Service); log($"  ✗ {target.Service} НЕ поднялся вовремя"); }
                }
                else log($"[dry] стоп/старт {target.Service}, ждать http://127.0.0.1:{target.Port}");
            }
            else
            {
                log($"→ Слот 0 = {tok.Serial}: нет привязки — службу не стартую, ридер снимаю");
            }

            // Снять ридер слота 0 → слотом 0 станет следующий физический токен.
            if (!dryRun)
            {
                int rv = readers.ForgetReader(reader0);
                log($"  forget '{reader0}' rv=0x{rv:X8}");
                Thread.Sleep(700); // дать драйверу переустановить нумерацию
            }
            else log($"[dry] forget '{reader0}'");
        }

        bool ok = failed.Count == 0 && started.Count == targets.Count(t => tokens.Any(k => k.Serial.Equals(t.ExpectedSerial, StringComparison.OrdinalIgnoreCase)));
        log(ok ? "=== Подъём завершён успешно ===" : $"=== Подъём завершён: поднято {started.Count}, ошибок {failed.Count} ===");
        return new Result(ok, started, failed, readerBySerial);
    }

    /// <summary>
    /// Подъём методом INTRODUCE (способ 2UTM, БЕЗ рестарта SCardSvr и БЕЗ живого
    /// PKCS11-скана) — работает из session 0. Требует заполненного ReaderName в
    /// конфиге (нативное имя ридера каждого токена).
    ///
    /// Хореография «по одному»: остановить все УТМ → forget всех ридеров → далее для
    /// каждого УТМ в порядке имени ридера: introduce ТОЛЬКО его ридер (значит его
    /// токен — единственный, т.е. slot 0) → старт службы (садится на свой токен,
    /// сверяем ФСРАР) → forget его ридер (его токен держит запущенный УТМ) → следующий.
    /// Так каждая служба при старте видит ровно один ридер = свой токен.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Result ApplyIntroduce(IReadOnlyList<Target> targets, Action<string> log, bool dryRun = false)
    {
        var started = new List<string>();
        var failed = new List<string>();
        var echo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var order = targets
            .Where(t => !string.IsNullOrEmpty(t.ReaderName))
            .OrderBy(t => t.ReaderName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (order.Count != targets.Count)
            log($"ВНИМАНИЕ: у {targets.Count - order.Count} УТМ нет ReaderName в конфиге — они пропущены.");
        if (order.Count == 0)
        {
            log("Нет ни одного ReaderName — introduce-подъём невозможен.");
            return new Result(false, started, targets.Select(t => t.Service).ToList(), echo);
        }

        log($"=== introduce-подъём ({(dryRun ? "dry-run" : "apply")}) для {order.Count} УТМ ===");
        foreach (var t in order)
        {
            echo[t.ExpectedSerial] = t.ReaderName!;
            log($"  план: {t.Service} :{t.Port} ← ридер '{t.ReaderName}' (serial {t.ExpectedSerial}, fsrar {t.Fsrar ?? "-"})");
        }
        if (dryRun) { log("dry-run: реальных действий не сделано."); return new Result(true, started, failed, echo); }

        // 0. Остановить все УТМ — чтобы ни один токен не был занят (чистый старт).
        foreach (var t in targets)
        {
            if (ServiceControl.GetState(t.Service) == ServiceState.Running)
            {
                log($"стоп {t.Service}");
                ServiceControl.Stop(t.Service, TimeSpan.FromSeconds(60));
            }
        }
        Thread.Sleep(1000);

        using var http = new UtmHttpClient(TimeSpan.FromSeconds(3));

        // Дождаться готовности SCardSvr (тёплый, StartType=Automatic) — БЕЗ рестарта.
        // На загрузке служба может стартовать раньше, чем SCardSvr примет вызовы.
        ReaderReset.EnsureScardReady(log, TimeSpan.FromSeconds(30));

        using var rt = new ReaderTable();

        // 1. Забыть все текущие ридеры (чистый лист). SCardSvr НЕ рестартим.
        int forgotten = rt.ForgetAllReaders();
        log($"forget всех ридеров: {forgotten}");
        Thread.Sleep(500);

        // 2. По одному: introduce → старт → forget.
        foreach (var t in order)
        {
            int rvI = rt.IntroduceReader(t.ReaderName!, t.ReaderName!);
            var present = rt.ListReaders();
            log($"→ {t.Service}: introduce '{t.ReaderName}' rv=0x{rvI:X8}; ридеров сейчас: {present.Count} [{string.Join(", ", present)}]");

            ServiceControl.Stop(t.Service, TimeSpan.FromSeconds(60));
            Thread.Sleep(300);
            bool run = ServiceControl.Start(t.Service, TimeSpan.FromSeconds(90));
            bool up = run && WaitHttp(http, t.Port, TimeSpan.FromSeconds(90), t.Fsrar, log);
            if (up) { started.Add(t.Service); log($"  ✓ {t.Service} готов, ФСРАР сошёлся"); }
            else { failed.Add(t.Service); log($"  ✗ {t.Service} НЕ поднялся / ФСРАР не тот"); }

            int rvF = rt.ForgetReader(t.ReaderName!);
            log($"  forget '{t.ReaderName}' rv=0x{rvF:X8}");
            Thread.Sleep(500);
        }

        // ВАЖНО: оставить все ридеры ВВЕДЁННЫМИ. Пустая PC/SC-таблица вводит SCardSvr
        // в состояние «Running, но новые контексты не принимает» (0x8010001D) — тогда
        // ломаются последующие операции (перезапуск/лечение). Токены заняты
        // работающими УТМ — введение алиасов им не мешает.
        foreach (var t in order) rt.IntroduceReader(t.ReaderName!, t.ReaderName!);
        log($"ридеры восстановлены в таблице: {rt.ListReaders().Count}");

        bool ok = failed.Count == 0 && started.Count == order.Count;
        log(ok ? "=== introduce-подъём успешно ===" : $"=== introduce-подъём: поднято {started.Count}, ошибок {failed.Count} ===");
        return new Result(ok, started, failed, echo);
    }

    /// <summary>
    /// Перезапуск ОДНОГО УТМ через introduce (без рестарта SCardSvr, без PKCS11-скана,
    /// работает из session 0). Остальные УТМ работают и не затрагиваются. Для «упал —
    /// перезапустить» и как финал обновления (после замены файлов).
    /// Хореография: стоп цель → forget всех ридеров (работающие УТМ держат токены,
    /// им это не мешает) → introduce ТОЛЬКО ридер цели (его свободный токен = slot 0)
    /// → старт → сверка ФСРАР → forget ридер цели.
    /// </summary>
    /// <param name="allReaders">Имена ридеров ВСЕХ УТМ — чтобы в конце вернуть полную
    /// таблицу (иначе SCardSvr впадает во флап «не принимает новые контексты»).</param>
    [SupportedOSPlatform("windows")]
    public static bool RestartOne(Target t, IReadOnlyList<string> allReaders, Action<string> log)
    {
        if (string.IsNullOrEmpty(t.ReaderName))
        {
            log($"{t.Service}: нет ReaderName в конфиге — introduce-перезапуск невозможен.");
            return false;
        }

        ReaderReset.EnsureScardReady(log, TimeSpan.FromSeconds(30));
        using var rt = new ReaderTable();
        using var http = new UtmHttpClient(TimeSpan.FromSeconds(3));

        log($"перезапуск {t.Service} :{t.Port} (ридер '{t.ReaderName}', ожидаю ФСРАР {t.Fsrar ?? "-"})");
        ServiceControl.Stop(t.Service, TimeSpan.FromSeconds(60));
        Thread.Sleep(500);

        // На время старта в таблице должен быть ТОЛЬКО ридер цели, иначе slot 0 —
        // чужой (занятый) токен. Forget всех (работающие УТМ держат токены, им это не
        // мешает) → introduce только цель → её свободный токен = slot 0.
        int nf = rt.ForgetAllReaders();
        int rvI = rt.IntroduceReader(t.ReaderName!, t.ReaderName!);
        var present = rt.ListReaders();
        log($"forget всех: {nf}; introduce '{t.ReaderName}' rv=0x{rvI:X8}; ридеров: {present.Count} [{string.Join(", ", present)}]");

        bool run = ServiceControl.Start(t.Service, TimeSpan.FromSeconds(90));
        bool up = run && WaitHttp(http, t.Port, TimeSpan.FromSeconds(90), t.Fsrar, log);

        // Вернуть полную таблицу ридеров (иначе флап SCardSvr). Токены заняты УТМ —
        // введение алиасов им не мешает.
        foreach (var r in allReaders.Where(r => !string.IsNullOrEmpty(r)).Distinct())
            rt.IntroduceReader(r, r);
        log($"ридеры восстановлены в таблице: {rt.ListReaders().Count}");

        log(up ? $"✓ {t.Service} перезапущен, ФСРАР сошёлся" : $"✗ {t.Service} НЕ поднялся / ФСРАР не тот");
        return up;
    }

    private static bool WaitHttp(UtmHttpClient http, int port, TimeSpan timeout, string? expectedFsrar, Action<string> log)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var info = http.GetInfoAsync(port).GetAwaiter().GetResult();
            if (info is not null && info.RsaOk)
            {
                if (!string.IsNullOrEmpty(expectedFsrar)
                    && !string.Equals(info.OwnerId, expectedFsrar, StringComparison.OrdinalIgnoreCase))
                {
                    log($"  ВНИМАНИЕ: порт {port} отвечает, но ownerId={info.OwnerId} ≠ ожидаемого {expectedFsrar}!");
                    return false; // поднялся не тот токен — это провал привязки
                }
                return true;
            }
            Thread.Sleep(1500);
        }
        return false;
    }
}
