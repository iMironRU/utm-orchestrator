using System.Runtime.Versioning;
using UtmOrchestrator.Core.Diagnostics;
using UtmOrchestrator.Core.Services;
using UtmOrchestrator.Core.State;

namespace UtmOrchestrator.Core.Health;

/// <summary>
/// Read-only оценка здоровья набора УТМ: состояние службы + /api/info/list →
/// вердикт (OK / Stopped / Faulty + причина). Ничего не меняет. Используется и
/// службой-наблюдателем, и панелью.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HealthChecker
{
    private readonly TimeSpan _httpTimeout;

    public HealthChecker(TimeSpan? httpTimeout = null)
        => _httpTimeout = httpTimeout ?? TimeSpan.FromSeconds(6);

    public async Task<IReadOnlyList<InstanceHealth>> CheckAsync(
        IEnumerable<UtmInstance> instances, CancellationToken ct = default)
    {
        var result = new List<InstanceHealth>();
        using var http = new UtmHttpClient(_httpTimeout);

        foreach (var inst in instances)
        {
            var state = ServiceControl.GetState(inst.ServiceName);
            UtmInfo? info = null;
            SigningHealth? signing = null;
            HealthVerdict verdict;
            string? reason;

            switch (state)
            {
                case ServiceState.NotInstalled:
                    verdict = HealthVerdict.Faulty;
                    reason = "служба не установлена";
                    break;

                case ServiceState.Stopped:
                    verdict = HealthVerdict.Stopped;
                    reason = "служба остановлена";
                    break;

                case ServiceState.StartPending:
                case ServiceState.StopPending:
                    verdict = HealthVerdict.Unknown;
                    reason = "служба меняет состояние";
                    break;

                default: // Running / Other
                    info = inst.Port > 0 ? await http.GetInfoAsync(inst.Port, ct).ConfigureAwait(false) : null;
                    // Реальная подпись по access_log (ловит сбой, невидимый в /api/info/list).
                    signing = SigningHealthReader.Read(inst.FolderPath, DateTimeOffset.Now);
                    (verdict, reason) = Evaluate(inst, info, signing);
                    break;
            }

            result.Add(new InstanceHealth(inst, state, info, verdict, reason, signing));
        }

        return result;
    }

    private static (HealthVerdict, string?) Evaluate(UtmInstance inst, UtmInfo? info, SigningHealth? signing)
    {
        if (info is null)
            return (HealthVerdict.Faulty, "не отвечает по HTTP (ещё грузится или завис)");

        if (!info.RsaOk)
        {
            // GOST valid + RSA невалиден = токен сел и читается, но нужен перевыпуск RSA
            // (типовое состояние после планового перевыпуска КЭП; RSA выпускается через УТМ).
            // Это НЕ поломка — не пугаем «сбоем» и не churn'им bring-up'ом.
            if (info.GostValid)
                return (HealthVerdict.NeedRsa, "нужен перевыпуск RSA (КЭП перевыпущен — старый RSA невалиден)");
            return (HealthVerdict.Faulty, "ошибка ключа RSA (не тот токен на слоте 0 при старте?)");
        }

        if (!string.IsNullOrEmpty(inst.ExpectedFsrar)
            && !string.Equals(info.OwnerId, inst.ExpectedFsrar, StringComparison.OrdinalIgnoreCase))
        {
            return (HealthVerdict.Faulty,
                $"привязан не тот токен: ожидался {inst.ExpectedFsrar}, читается {info.OwnerId ?? "неизвестно"}");
        }

        if (!info.GostValid)
            return (HealthVerdict.Faulty, "ГОСТ-сертификат недоступен/невалиден");

        // На бумаге всё в порядке (RSA/ГОСТ valid, свой ФСРАР). Но /api/info/list НЕ видит,
        // реально ли УТМ ПОДПИСЫВАЕТ. Сверяемся с access_log: если свежие POST /opt/in падают —
        // это ровно тот класс сбоя, что был невидим (рассинхрон RSA↔ГОСТ / крипто-DLL).
        if (signing is not null && signing.IsBroken)
        {
            return signing.ErrorClass switch
            {
                // 500/362 «ГОСТ не соответствует RSA» — то же лечение, что и load-time NeedRsa.
                SigningErrorClass.RsaGostMismatch => (HealthVerdict.NeedRsa,
                    "подпись падает: RSA не соответствует ГОСТ (перевыпущен КЭП) — перевыпустите RSA"),
                // 500/89 — контенция общей GOST-DLL / CKR: лечится gost-isolate.
                SigningErrorClass.CryptoLib => (HealthVerdict.SigningBroken,
                    "подпись падает: ошибка криптобиблиотеки (CKR) — примените gost-isolate"),
                _ => (HealthVerdict.SigningBroken,
                    $"подпись падает: POST /opt/in → {signing.LastCode} (см. access_log / веб УТМ)"),
            };
        }

        return (HealthVerdict.Ok, null);
    }
}
