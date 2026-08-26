using System.Net;
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
/// Самообновление качает app; runtime — только если ключ отличается от установленного.
///
/// ВАЖНО: GitHub в РФ часто доступен только через локальный прокси пользователя — берём его
/// из <see cref="GitHubProxy"/> (служба-LocalSystem иначе ходит «напрямую» и ловит 403).
/// </summary>
public static class UpdateChecker
{
    private const string LatestApi = "https://api.github.com/repos/iMironRU/utm-orchestrator/releases/latest";
    // Веб-эндпоинт (github.com, не api.*): 302 на .../releases/tag/vX. Фолбэк, если api.* режут.
    private const string LatestWeb = "https://github.com/iMironRU/utm-orchestrator/releases/latest";

    private static HttpClient NewClient(IWebProxy? proxy, bool followRedirects)
    {
        var h = new SocketsHttpHandler
        {
            UseProxy = proxy is not null,
            Proxy = proxy,
            AllowAutoRedirect = followRedirects,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("UtmOrchestrator"); // UA обязателен для API GitHub
        return c;
    }

    public sealed record Info(
        string Current, string? Latest, bool UpdateAvailable,
        string? AppUrl, string? RuntimeUrl, string? RuntimeKey, bool Reachable, string? Error = null);

    private static readonly Regex RuntimeName =
        new(@"^UtmOrchestrator-runtime-([0-9a-f]+)-win-x64\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<Info> CheckAsync(CancellationToken ct = default)
    {
        string current = AppInfo.Version;
        var (proxy, proxySource) = GitHubProxy.Resolve(); // прокси на момент проверки
        using var http = NewClient(proxy, followRedirects: true);
        try
        {
            string json = await http.GetStringAsync(LatestApi, ct).ConfigureAwait(false);
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
                using var web = NewClient(proxy, followRedirects: false);
                using var resp = await web.GetAsync(LatestWeb, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var loc = resp.Headers.Location?.ToString() ?? "";
                var m = Regex.Match(loc, @"/tag/v?([0-9][0-9.]*)");
                if (m.Success)
                {
                    string latest = m.Groups[1].Value;
                    bool newer = Version.TryParse(latest, out var lv)
                              && Version.TryParse(current, out var cv) && lv > cv;
                    // URL детерминирован (github.com). runtime-ключ без API неизвестен → авто-
                    // применение только app (apply качает рантайм лишь при RuntimeUrl!=null).
                    string appUrl = "https://github.com/iMironRU/utm-orchestrator/releases/download/v"
                                  + latest + "/UtmOrchestrator-app-" + latest + ".zip";
                    return new Info(current, latest, newer, appUrl, null, null, Reachable: true,
                        Error: "api.github.com недоступен, версия через github.com (прокси: " + proxySource + ")");
                }
                return new Info(current, null, false, null, null, null, Reachable: false,
                    Error: $"api: {apiEx.Message} | github.com без редиректа (прокси: {proxySource})");
            }
            catch (Exception webEx)
            {
                return new Info(current, null, false, null, null, null, Reachable: false,
                    Error: $"нет связи с GitHub (прокси: {proxySource}). api: {apiEx.Message} | web: {webEx.Message}");
            }
        }
    }
}
