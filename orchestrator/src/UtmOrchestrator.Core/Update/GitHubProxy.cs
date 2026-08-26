using System.Net;
using Microsoft.Win32;

namespace UtmOrchestrator.Core.Update;

/// <summary>
/// Прокси для ВНЕШНИХ запросов к GitHub (проверка обновлений + загрузка релизов).
///
/// Проблема: в РФ GitHub часто доступен только через локальный прокси (обход блокировок),
/// который пользователь запускает в СВОЕЙ сессии (напр. 127.0.0.1:PORT) — браузер берёт его
/// из WinINET (HKCU\...\Internet Settings). Служба оркестратора работает как LocalSystem и
/// НЕ видит этот прокси: её собственный HKCU пуст, а WinHTTP-прокси машины — «прямой».
/// Прямой доступ к api.github.com отдаёт 403.
///
/// Решение: читаем WinINET-прокси залогиненного пользователя напрямую из HKEY_USERS\&lt;SID&gt;
/// (LocalSystem видит все загруженные кусты) и ходим на GitHub через него. LocalSystem МОЖЕТ
/// подключиться к 127.0.0.1:PORT пользовательского прокси (loopback не изолирован по сессиям).
/// </summary>
public static class GitHubProxy
{
    /// <summary>Эффективный прокси для GitHub, либо null (прямой доступ). Плюс краткое описание источника.</summary>
    public static (IWebProxy? Proxy, string Source) Resolve()
    {
        if (OperatingSystem.IsWindows())
        {
            var user = UserWinInetProxy();
            if (user is not null) return (new WebProxy(user) { UseDefaultCredentials = true }, "user WinINET " + user);
            try
            {
                var sys = WebRequest.GetSystemWebProxy();
                var test = sys?.GetProxy(new Uri("https://api.github.com"));
                if (test is not null && test.Host != "api.github.com")
                    return (sys, "system " + test);
            }
            catch { }
        }
        return (null, "direct");
    }

    // WinINET-прокси активного пользователя из HKEY_USERS\<SID>\...\Internet Settings.
    private static string? UserWinInetProxy()
    {
        try
        {
            foreach (var sid in Registry.Users.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue; // реальные пользователи
                if (sid.EndsWith("_Classes", StringComparison.Ordinal)) continue;
                using var isk = Registry.Users.OpenSubKey(
                    sid + @"\Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                if (isk is null) continue;
                if ((isk.GetValue("ProxyEnable") as int? ?? 0) != 1) continue;
                if (isk.GetValue("ProxyServer") is not string server || string.IsNullOrWhiteSpace(server)) continue;

                // ProxyServer: "host:port" ИЛИ "http=host:port;https=host:port;..."
                string p = server;
                if (p.Contains('='))
                {
                    var parts = p.Split(';');
                    string? pick = Array.Find(parts, x => x.StartsWith("https=", StringComparison.OrdinalIgnoreCase))
                                ?? Array.Find(parts, x => x.StartsWith("http=", StringComparison.OrdinalIgnoreCase));
                    if (pick is null) continue;
                    p = pick[(pick.IndexOf('=') + 1)..];
                }
                p = p.Trim();
                if (p.Length == 0) continue;
                if (!p.Contains("://")) p = "http://" + p;
                return p;
            }
        }
        catch { }
        return null;
    }
}
