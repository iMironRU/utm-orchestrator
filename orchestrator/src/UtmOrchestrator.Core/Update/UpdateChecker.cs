using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UtmOrchestrator.Core.Update;

/// <summary>
/// Проверяет последний релиз оркестратора на GitHub и сравнивает с текущей версией.
/// Каждый оркестратор проверяет сам — поэтому две (и больше) машины видят обновление
/// независимо, без внешнего «обновлятора».
///
/// Релиз содержит ДВА артефакта:
///   UtmOrchestrator-app-&lt;версия&gt;.zip           — наш код (~5 МБ, каждый релиз)
///   UtmOrchestrator-runtime-&lt;key&gt;-win-x64.zip   — общий .NET-рантайм (~65 МБ, редко)
/// Самообновление качает app; runtime — только если ключ отличается от установленного
/// (файл runtime.key рядом с exe). Так рантайм не скачивается «каждый раз».
/// </summary>
public static class UpdateChecker
{
    private const string LatestApi = "https://api.github.com/repos/iMironRU/utm-orchestrator/releases/latest";
    // Веб-эндпоинт релиза (github.com, не api.*): 302 на .../releases/tag/vX. Фолбэк, если
    // api.github.com режут, а github.com открыт (типично для корп-фильтрации). Даёт версию.
    private const string LatestWeb = "https://github.com/iMironRU/utm-orchestrator/releases/latest";

    // ВНЕШНИЙ хост (GitHub): УВАЖАЕМ системный прокси (как браузер) + доменные креды —
    // иначе на машине за прокси проверка не достучится (браузер ходит, а мы — нет).
    // Таймаут короткий (8с): если GitHub недоступен — быстро вернуть «нет связи», а не висеть.
    private static readonly HttpClient _http = CreateClient(true);
    private static readonly HttpClient _httpNoRedirect = CreateClient(false);

    private static HttpClient CreateClient(bool followRedirects)
    {
        var h = new SocketsHttpHandler
        {
            UseProxy = true,
            AllowAutoRedirect = followRedirects,
            DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials,
        };
        try { h.Proxy = System.Net.WebRequest.GetSystemWebProxy(); } catch { }
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("UtmOrchestrator");
        return c;
    }

    public sealed record Info(
        string Current, string? Latest, bool UpdateAvailable,
        string? AppUrl, string? RuntimeUrl, string? RuntimeKey, bool Reachable, string? Error = null);

    // Ключ рантайма из имени ассета: UtmOrchestrator-runtime-<key>-win-x64.zip
    private static readonly Regex RuntimeName =
        new(@"^UtmOrchestrator-runtime-([0-9a-f]+)-win-x64\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

            string? appUrl = null, runtimeUrl = null, runtimeKey = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    string url = a.GetProperty("browser_download_url").GetString() ?? "";
                    if (name.StartsWith("UtmOrchestrator-app", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        appUrl = url;
                    var m = RuntimeName.Match(name);
                    if (m.Success) { runtimeUrl = url; runtimeKey = m.Groups[1].Value; }
                }

            bool newer = Version.TryParse(latest, out var lv)
                      && Version.TryParse(current, out var cv) && lv > cv;
            return new Info(current, string.IsNullOrEmpty(latest) ? null : latest,
                newer && appUrl != null, appUrl, runtimeUrl, runtimeKey, Reachable: true);
        }
        catch (Exception apiEx)
        {
            // api.github.com не ответил (режут/прокси/таймаут). Пробуем github.com —
            // веб-редирект на .../releases/tag/vX; часто открыт, когда api.* закрыт.
            try
            {
                using var resp = await _httpNoRedirect.GetAsync(
                    LatestWeb, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var loc = resp.Headers.Location?.ToString() ?? "";
                var m = Regex.Match(loc, @"/tag/v?([0-9][0-9.]*)");
                if (m.Success)
                {
                    string latest = m.Groups[1].Value;
                    bool newer = Version.TryParse(latest, out var lv)
                              && Version.TryParse(current, out var cv) && lv > cv;
                    // URL ассетов детерминированы (github.com, не api.*). runtime-ключ без API
                    // неизвестен → авто-применение только app (рантайм меняется редко; apply
                    // качает рантайм лишь при RuntimeUrl!=null, иначе оставляет установленный).
                    string appUrl = "https://github.com/iMironRU/utm-orchestrator/releases/download/v"
                                  + latest + "/UtmOrchestrator-app-" + latest + ".zip";
                    return new Info(current, latest, newer && Version.TryParse(latest, out _), appUrl, null, null,
                        Reachable: true, Error: "api.github.com недоступен, версия через github.com (" + apiEx.Message + ")");
                }
                return new Info(current, null, false, null, null, null, Reachable: false,
                    Error: "api: " + apiEx.Message + " | github.com без редиректа на тег");
            }
            catch (Exception webEx)
            {
                // Ни api.github.com, ни github.com — реально нет связи. Причину отдаём в UI/лог.
                return new Info(current, null, false, null, null, null, Reachable: false,
                    Error: "api: " + apiEx.Message + " | web: " + webEx.Message);
            }
        }
    }
}
