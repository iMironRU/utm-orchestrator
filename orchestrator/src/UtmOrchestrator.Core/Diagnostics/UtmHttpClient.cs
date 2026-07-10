using System.Text.Json;

namespace UtmOrchestrator.Core.Diagnostics;

/// <summary>
/// Читает диагностику УТМ по HTTP (localhost:port). Самый информативный эндпоинт —
/// /api/info/list (rsaError, ownerId, db.ownerId, rsa/gost). Всё локально, без TLS.
/// </summary>
public sealed class UtmHttpClient : IDisposable
{
    private readonly HttpClient _http;

    public UtmHttpClient(TimeSpan? timeout = null)
    {
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(8) };
    }

    /// <summary>Читает /api/info/list. Возвращает null, если УТМ не ответил.</summary>
    public async Task<UtmInfo?> GetInfoAsync(int port, CancellationToken ct = default)
    {
        string url = $"http://localhost:{port}/api/info/list";
        string json;
        try
        {
            json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
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

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsCertValid(JsonElement root, string certKey)
    {
        if (!root.TryGetProperty(certKey, out var cert) || cert.ValueKind != JsonValueKind.Object)
            return false;
        return GetString(cert, "isValid") == "valid";
    }

    public void Dispose() => _http.Dispose();
}
