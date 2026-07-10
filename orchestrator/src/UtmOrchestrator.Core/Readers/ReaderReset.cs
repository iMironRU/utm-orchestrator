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

        // 2. Забыть все алиасы ридеров.
        using (var table = new ReaderTable())
        {
            int n = table.ForgetAllReaders();
            L($"Забыто ридеров: {n}");
        }

        // 3. Перезапустить SCardSvr → нативное перечисление токенов.
        L("Перезапуск SCardSvr…");
        ServiceControl.Stop("SCardSvr", TimeSpan.FromSeconds(30));
        ServiceControl.Start("SCardSvr", TimeSpan.FromSeconds(30));
        L("Ридеры сброшены в нативное состояние");
    }
}
