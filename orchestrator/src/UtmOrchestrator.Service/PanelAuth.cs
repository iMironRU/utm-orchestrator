using System.Security.Cryptography;

namespace UtmOrchestrator.Service;

/// <summary>
/// Сессии панели: при успешном входе выдаём случайный токен (кука HttpOnly), храним
/// набор валидных токенов в памяти. Рестарт службы = токены сбрасываются (нужен
/// повторный вход) — приемлемо для локальной панели. Для внешнего доступа лучше TLS
/// (иначе кука/пароль идут по сети открыто) — VPN/туннель или отдельный шаг.
/// </summary>
public static class PanelAuth
{
    public const string Cookie = "utmo_session";
    private static readonly HashSet<string> _sessions = new();
    private static readonly object _lock = new();

    public static string Issue()
    {
        string tok = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        lock (_lock) _sessions.Add(tok);
        return tok;
    }

    public static bool Valid(string? tok)
    {
        if (string.IsNullOrEmpty(tok)) return false;
        lock (_lock) return _sessions.Contains(tok);
    }

    public static void Revoke(string? tok)
    {
        if (string.IsNullOrEmpty(tok)) return;
        lock (_lock) _sessions.Remove(tok);
    }
}
