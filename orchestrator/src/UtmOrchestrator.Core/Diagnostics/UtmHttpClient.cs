using System.Net.Http;
using System.Text.Json;

namespace UtmOrchestrator.Core.Diagnostics;

/// <summary>
/// Читает диагностику УТМ по HTTP (localhost:port). Самый информативный эндпоинт —
/// /api/info/list (rsaError, ownerId, db.ownerId, rsa/gost). Всё локально, без TLS.
/// </summary>
public sealed class UtmHttpClient : IDisposable
{
    // ВАЖНО: ОДИН общий HttpClient на весь процесс с ЯВНО ОТКЛЮЧЁННЫМ прокси.
    //
    // 1) UseProxy=false — КРИТИЧНО. По умолчанию HttpClient берёт системный/WinHTTP
    //    или прокси из переменных окружения (HTTP_PROXY). Если прокси настроен, даже
    //    запросы к 127.0.0.1 к нашим же УТМ уходят через прокси и, когда тот их не
    //    обслуживает, висят до таймаута (~8с на вызов → опрос статуса ~16с). Панель к
    //    локальным УТМ обязана ходить НАПРЯМУЮ. Это же спасает на корпоративной машине
    //    с системным прокси.
    // 2) Один общий клиент переиспользует пул соединений — без шторма новых сокетов к
    //    УТМ при частых опросах.
    // Таймаут задаётся на вызов через linked CancellationToken (Timeout клиента снят).
    private static readonly HttpClient _shared = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private readonly TimeSpan _timeout;

    public UtmHttpClient(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    // ВАЖНО: используем 127.0.0.1, а не "localhost". На Windows .NET HttpClient по
    // имени localhost сначала пробует IPv6 (::1) и, если УТМ там не слушает, ждёт
    // connect-timeout перед откатом на IPv4 — это добавляло ~1с на КАЖДОЕ новое
    // подключение. С 127.0.0.1 подключение мгновенно.
    private const string Host = "127.0.0.1";

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        return await _shared.GetStringAsync(url, cts.Token).ConfigureAwait(false);
    }

    /// <summary>Читает /api/info/list. Возвращает null, если УТМ не ответил.</summary>
    public async Task<UtmInfo?> GetInfoAsync(int port, CancellationToken ct = default)
    {
        string url = $"http://{Host}:{port}/api/info/list";
        string json;
        try
        {
            json = await GetStringAsync(url, ct).ConfigureAwait(false);
        }
        catch
        {
            return null; // не поднялся / не отвечает
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = GetString(root, "version");
            string? ownerId = GetString(root, "ownerId");
            string? rsaError = GetString(root, "rsaError");

            string? dbOwnerId = null;
            if (root.TryGetProperty("db", out var db) && db.ValueKind == JsonValueKind.Object)
                dbOwnerId = GetString(db, "ownerId");

            bool gostValid = IsCertValid(root, "gost");
            bool rsaValid = IsCertValid(root, "rsa");

            return new UtmInfo(version, ownerId, dbOwnerId, rsaError, gostValid, rsaValid);
        }
        catch
        {
            return null; // ответ есть, но не JSON/не тот формат
        }
    }

    /// <summary>
    /// Читает орг-данные из /api/rsa/orginfo (организация, ИНН) и /api/gost/orginfo
    /// (ФИО, адресные поля). Возвращает null, если ничего не удалось прочитать.
    /// </summary>
    public async Task<UtmOrgInfo?> GetOrgInfoAsync(int port, CancellationToken ct = default)
    {
        JsonElement? rsa = await TryGetJsonAsync($"http://{Host}:{port}/api/rsa/orginfo", ct).ConfigureAwait(false);
        JsonElement? gost = await TryGetJsonAsync($"http://{Host}:{port}/api/gost/orginfo", ct).ConfigureAwait(false);
        if (rsa is null && gost is null) return null;

        string? organization = rsa is { } r ? GetString(r, "o") : null;
        string? inn = rsa is { } r2 ? GetString(r2, "ou") : null;
        string? personName = gost is { } g ? GetString(g, "cn") : null;

        string? address = null;
        if (gost is { } g2)
        {
            var parts = new[] { GetString(g2, "street"), GetString(g2, "l"), GetString(g2, "st") }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var joined = string.Join(", ", parts);
            if (joined.Length > 0) address = joined;
        }

        return new UtmOrgInfo(organization, inn, personName, address);
    }

    private async Task<JsonElement?> TryGetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            string json = await GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsCertValid(JsonElement root, string certKey)
    {
        if (!root.TryGetProperty(certKey, out var cert) || cert.ValueKind != JsonValueKind.Object)
            return false;
        return GetString(cert, "isValid") == "valid";
    }

    // Общий HttpClient живёт всё время работы процесса — здесь ничего не освобождаем.
    public void Dispose() { }
}
