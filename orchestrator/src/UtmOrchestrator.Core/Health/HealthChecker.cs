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
            string? rsaBoundGost = null, currentGost = null;
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
                    // Детерминированный детектор рассинхрона RSA↔ГОСТ по отпечаткам (точнее эвристики по 500).
                    if (inst.Port > 0 && info is not null)
                        (rsaBoundGost, currentGost) = await RsaGostCache.GetAsync(http, inst.Port, ct).ConfigureAwait(false);
                    (verdict, reason) = Evaluate(inst, info, signing, rsaBoundGost, currentGost);
                    break;
            }

            result.Add(new InstanceHealth(inst, state, info, verdict, reason, signing, rsaBoundGost, currentGost));
        }

        return result;
    }

    private static (HealthVerdict, string?) Evaluate(UtmInstance inst, UtmInfo? info, SigningHealth? signing,
        string? rsaBoundGost = null, string? currentGost = null)
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

        // ДЕТЕРМИНИРОВАННЫЙ детектор рассинхрона RSA↔ГОСТ по отпечаткам (точнее эвристики по 500 в логе):
        // RSA сгенерирован под один ГОСТ-отпечаток, а на токене сейчас другой ⇒ «ГОСТ не соответствует RSA».
        // Частый случай — кросс-привязка: RSA перевыпустили в момент тряски токенов и он привязался к ГОСТ соседа.
        if (!string.IsNullOrEmpty(rsaBoundGost) && !string.IsNullOrEmpty(currentGost)
            && !string.Equals(rsaBoundGost, currentGost, StringComparison.OrdinalIgnoreCase))
        {
            static string Sh(string fp) => fp.Length > 8 ? fp[..8] : fp;
            return (HealthVerdict.NeedRsa,
                $"RSA привязан к другому ГОСТ (RSA→{Sh(rsaBoundGost!)} ≠ токен {Sh(currentGost!)}) — перевыпустите RSA на стабильном парке");
        }

        // На бумаге всё в порядке (RSA/ГОСТ valid, свой ФСРАР, отпечатки совпали). Но /api/info/list НЕ видит,
        // реально ли УТМ ПОДПИСЫВАЕТ. Сверяемся с access_log: если свежие POST /opt/in падают —
        // это ровно тот класс сбоя, что был невидим (крипто-DLL/CKR и т.п.).
        if (signing is not null && signing.ActivelyBroken)
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

        // Последняя подпись была ошибкой, но давно и без подтверждающего успеха → НЕ врём «Работает».
        // Это чинит ложное «OK» в тихие периоды (старый сбой выпал из окна, свежих попыток нет).
        if (signing is not null && signing.Unconfirmed)
        {
            return (HealthVerdict.SigningUnconfirmed,
                "подпись не подтверждена: последняя попытка — ошибка, успеха с тех пор не было — проверьте (тест/перевыпуск RSA)");
        }

        return (HealthVerdict.Ok, null);
    }
}
