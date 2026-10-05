using System.Collections.Concurrent;

namespace UtmOrchestrator.Core.Diagnostics;

/// <summary>
/// Кэш отпечатков RSA↔ГОСТ по порту УТМ. Отпечатки меняются только при перевыпуске RSA
/// (редко), а health-проверка идёт часто (панель опрашивает ~каждые 8с) — поэтому кэшируем
/// на 60с, чтобы не дёргать /api/rsa/orginfo + /api/gost/orginfo на каждый опрос.
/// Детектор детерминированный: RsaBoundGost != CurrentGost ⇒ «ГОСТ не соответствует RSA».
/// </summary>
public static class RsaGostCache
{
    private sealed record Entry(string? RsaBoundGost, string? CurrentGost, DateTime AtUtc);

    private static readonly ConcurrentDictionary<int, Entry> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>Отпечатки для порта (из кэша, если свежие; иначе читает у УТМ и кэширует).</summary>
    public static async Task<(string? RsaBoundGost, string? CurrentGost)> GetAsync(
        UtmHttpClient http, int port, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(port, out var e) && DateTime.UtcNow - e.AtUtc < Ttl)
            return (e.RsaBoundGost, e.CurrentGost);

        var (rsaFp, gostFp) = await http.GetRsaGostFingerprintsAsync(port, ct).ConfigureAwait(false);
        _cache[port] = new Entry(rsaFp, gostFp, DateTime.UtcNow);
        return (rsaFp, gostFp);
    }

    /// <summary>Сбросить кэш порта — вызывать сразу после перевыпуска RSA, чтобы статус обновился без ожидания TTL.</summary>
    public static void Invalidate(int port) => _cache.TryRemove(port, out _);
}
