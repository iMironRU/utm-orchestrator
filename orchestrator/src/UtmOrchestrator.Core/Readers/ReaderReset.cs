using System.Runtime.Versioning;
using UtmOrchestrator.Core.Services;

namespace UtmOrchestrator.Core.Readers;

/// <summary>
/// Программный сброс PC/SC-таблицы к нативному состоянию (проверено, NOTES §6.14):
/// забыть все алиасы ридеров + перезапустить SCardSvr → драйвер заново перечисляет
/// физические токены с нативными именами по порядку USB-портов. Заменяет физическое
/// переподключение токенов и «будит» токены, которые система «не видит».
///
/// ВНИМАНИЕ: деструктивно — останавливает зависящие от SCardSvr службы Transport*.
/// Вызывать только в рамках контролируемого bring-up/recovery, не на ходу.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ReaderReset
{
    public static void ResetToNative(IEnumerable<string> dependentServices, Action<string>? log = null)
    {
        void L(string m) => log?.Invoke(m);

        // 1. Остановить зависящие от SCardSvr службы (иначе SCardSvr не остановить).
        foreach (var svc in dependentServices)
        {
            if (ServiceControl.GetState(svc) == ServiceState.Running)
            {
                L($"Останавливаю {svc}");
                ServiceControl.Stop(svc, TimeSpan.FromSeconds(60));
            }
        }

        // 2. КРИТИЧНО: гарантировать, что SCardSvr запущен и готов, ДО первого
        //    SCard-вызова. SCardSvr — demand-start и сам останавливается по
        //    бездействию: когда все УТМ (его клиенты) стоят — например, сразу после
        //    загрузки или если мы их только что остановили — он выключен, и
        //    SCardEstablishContext падает с 0x8010001D (SCARD_E_NO_SERVICE). Именно
        //    это роняло подъём из задачи/службы (а не «session 0», как казалось).
        EnsureScardReady(L, TimeSpan.FromSeconds(30));

        // Забыть все алиасы ридеров.
        using (var table = new ReaderTable())
        {
            int n = table.ForgetAllReaders();
            L($"Забыто ридеров: {n}");
        }

        // 3. Перезапустить SCardSvr → нативное перечисление токенов.
        L("Перезапуск SCardSvr…");
        ServiceControl.Stop("SCardSvr", TimeSpan.FromSeconds(30));
        ServiceControl.Start("SCardSvr", TimeSpan.FromSeconds(30));

        // 4. КРИТИЧНО: после рестарта SCardSvr «Running» ещё не значит, что менеджер
        //    ресурсов принимает вызовы — сразу после старта SCardEstablishContext
        //    отдаёт 0x8010001D (SCARD_E_NO_SERVICE). Ждём реальной готовности, иначе
        //    последующий скан/forget падают (ловилось и в интерактивной сессии, и в
        //    службе). Ретраим установку контекста + перечисление ридеров.
        WaitScardReady(L, TimeSpan.FromSeconds(30));
        L("Ридеры сброшены в нативное состояние");
    }

    /// <summary>
    /// Убедиться, что SCardSvr запущен (стартовать, если стоит) и реально принимает
    /// вызовы. Вызывать перед первым SCard-обращением. Рестарт НЕ делает.
    /// </summary>
    public static void EnsureScardReady(Action<string>? log, TimeSpan timeout)
    {
        if (ServiceControl.GetState("SCardSvr") != ServiceState.Running)
        {
            log?.Invoke("SCardSvr остановлен (нет клиентов) — запускаю…");
            try { ServiceControl.Start("SCardSvr", TimeSpan.FromSeconds(30)); }
            catch (Exception e) { log?.Invoke($"старт SCardSvr: {e.Message}"); }
        }
        WaitScardReady(log, timeout);
    }

    /// <summary>Дождаться, пока PC/SC начнёт принимать вызовы после рестарта SCardSvr.</summary>
    private static void WaitScardReady(Action<string>? log, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                using var table = new ReaderTable();
                var readers = table.ListReaders();
                if (readers.Count > 0)
                {
                    log?.Invoke($"PC/SC готов: ридеров {readers.Count} (попытка {attempt})");
                    return;
                }
            }
            catch (Exception e)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    log?.Invoke($"PC/SC так и не готов за {timeout.TotalSeconds:F0}с: {e.Message}");
                    return;
                }
            }
            if (DateTime.UtcNow >= deadline)
            {
                log?.Invoke("PC/SC: ридеры не появились в отведённое время — продолжаю.");
                return;
            }
            Thread.Sleep(1000);
        }
    }
}
