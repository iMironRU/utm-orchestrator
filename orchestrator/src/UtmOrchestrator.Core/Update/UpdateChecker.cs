using System.Net.Http;
using System.Text.Json;

namespace UtmOrchestrator.Core.Update;

/// <summary>
/// Проверяет последний релиз оркестратора на GitHub и сравнивает с текущей версией.
/// Каждый оркестратор проверяет сам — поэтому две (и больше) машины видят обновление
/// независимо, без внешнего «обновлятора».
/// </summary>
public static class UpdateChecker
{
    private const string LatestApi = "https://api.github.com/repos/iMironRU/utm-orchestrator/releases/latest";

    // UseProxy=false: унаследованный HTTP_PROXY не должен мешать; UA обязателен для API GitHub.
    // Таймаут короткий (8с): если GitHub недоступен — быстро вернуть «нет связи», а не висеть.
    private static readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(8) };

    static UpdateChecker() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("UtmOrchestrator");

    public sealed record Info(string Current, string? Latest, bool UpdateAvailable, string? PayloadUrl, bool Reachable);

    public static async Task<Info> CheckAsync(CancellationToken ct = default)
    {
        string current = AppInfo.Version;
        try
        {
            string json = await _http.GetStringAsync(LatestApi, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string latest = tag.TrimStart('v', 'V');

            string? payloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    if (name.StartsWith("UtmOrchestrator-win-x64", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        payloadUrl = a.GetProperty("browser_download_url").GetString();
                }

            bool newer = Version.TryParse(latest, out var lv)
                      && Version.TryParse(current, out var cv) && lv > cv;
            return new Info(current, string.IsNullOrEmpty(latest) ? null : latest, newer && payloadUrl != null, payloadUrl, Reachable: true);
        }
        catch
        {
            // GitHub недоступен (нет сети/таймаут/блокировка) — Reachable=false, чтобы UI
            // показал «не удалось проверить» с кнопкой повтора, а не вечное «проверка…».
            return new Info(current, null, false, null, Reachable: false);
        }
    }
}
