using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UtmOrchestrator.Core.Diagnostics;

/// <summary>Класс ошибки подписи, распознанный по ответу УТМ на POST /opt/in/*.</summary>
public enum SigningErrorClass
{
    /// <summary>Свежих POST нет — судить не по чему (не флажим).</summary>
    NoData,
    /// <summary>Последний POST — 200: подпись работает.</summary>
    Healthy,
    /// <summary>500, тело ≈362: «ГОСТ сертификат не соответствует RSA» — нужен перевыпуск RSA.</summary>
    RsaGostMismatch,
    /// <summary>500, тело ≈89: ошибка криптобиблиотеки (CKR/контенция общей GOST-DLL) — нужен gost-isolate.</summary>
    CryptoLib,
    /// <summary>500 иной природы — причина не распознана по размеру.</summary>
    Unknown500,
}

/// <summary>
/// Итог по «здоровью подписи» одного УТМ, вычисленный по его access_log: реально ли
/// УТМ ПОДПИСЫВАЕТ исходящие (POST /opt/in/*), а не только «отвечает по HTTP». Ловит
/// класс сбоя, невидимый в /api/info/list (УТМ Running, RSA/ГОСТ по отдельности valid,
/// но подпись падает — рассинхрон RSA↔ГОСТ после перевыпуска КЭП, либо контенция крипто-DLL).
/// </summary>
public sealed record SigningHealth(
    SigningErrorClass ErrorClass,
    DateTimeOffset? LastPostUtc,
    int LastCode,
    int LastSize,
    int Recent200,
    int Recent500)
{
    public bool IsBroken => ErrorClass is SigningErrorClass.RsaGostMismatch
        or SigningErrorClass.CryptoLib or SigningErrorClass.Unknown500;

    public static readonly SigningHealth None =
        new(SigningErrorClass.NoData, null, 0, 0, 0, 0);
}

/// <summary>
/// Read-only анализ access_log УТМ. Читает только ХВОСТ файла (лог бывает многомегабайтным),
/// парсит свежие строки <c>POST /opt/in/&lt;тип&gt; … КОД РАЗМЕР</c> в пределах окна и делает вывод
/// по последней записи. Ничего не меняет и не сетевой — только файл.
/// </summary>
public static class SigningHealthReader
{
    // 95.78.238.237 - - [15/Sep/2026:20:16:02 +0500] "POST /opt/in/QueryResendDoc HTTP/1.1" 500 362
    private static readonly Regex Line = new(
        @"\[(?<ts>\d{2}/[A-Za-z]{3}/\d{4}:\d{2}:\d{2}:\d{2} [+-]\d{4})\]\s+""POST /opt/in/\S+ HTTP/1\.[01]""\s+(?<code>\d{3})\s+(?<size>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string TsFormat = "dd/MMM/yyyy:HH:mm:ss zzz";

    // Размеры тел (в байтах) двух известных ответов-ошибок УТМ.
    private const int SizeRsaMismatch = 362; // «ГОСТ сертификат не соответствует RSA сертификату…»
    private const int SizeCryptoLib = 89;    // ошибка переинициализации/криптобиблиотеки (CKR)

    /// <summary>
    /// Оценить подпись по access_log УТМ в папке <paramref name="folderPath"/>.
    /// <paramref name="window"/> — насколько «свежими» должны быть POST, чтобы им верить
    /// (по умолчанию 30 мин): старый 500, после которого починили и трафика ещё не было,
    /// НЕ должен вечно светить сбоем. Ошибки чтения → <see cref="SigningHealth.None"/> (не флажим).
    /// </summary>
    public static SigningHealth Read(string folderPath, DateTimeOffset now, TimeSpan? window = null,
        int tailBytes = 256 * 1024)
    {
        var win = window ?? TimeSpan.FromMinutes(30);
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return SigningHealth.None;
            var logDir = Path.Combine(folderPath, "transporter", "l");
            if (!Directory.Exists(logDir)) return SigningHealth.None;

            // Сегодняшний по локальной дате УТМ; если пусто/нет — берём самый свежий access_log.*.
            var today = Path.Combine(logDir, $"access_log.{now:yyyy-MM-dd}.log");
            string? file = File.Exists(today) ? today : LatestAccessLog(logDir);
            if (file is null) return SigningHealth.None;

            var tail = ReadTail(file, tailBytes);
            if (tail.Length == 0) return SigningHealth.None;

            DateTimeOffset? lastTs = null;
            int lastCode = 0, lastSize = 0, n200 = 0, n500 = 0;

            foreach (var raw in tail.Split('\n'))
            {
                var m = Line.Match(raw);
                if (!m.Success) continue;
                if (!DateTimeOffset.TryParseExact(m.Groups["ts"].Value, TsFormat,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                    continue;
                if (now - ts > win) continue; // вне окна свежести — пропускаем

                int code = int.Parse(m.Groups["code"].Value, CultureInfo.InvariantCulture);
                int size = int.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture);
                if (code == 200) n200++;
                else if (code >= 500) n500++;

                // строки в хронологическом порядке — последняя подходящая и есть «последняя»
                if (lastTs is null || ts >= lastTs) { lastTs = ts; lastCode = code; lastSize = size; }
            }

            if (lastTs is null) return SigningHealth.None; // свежих POST нет

            var cls = lastCode == 200
                ? SigningErrorClass.Healthy
                : lastCode >= 500
                    ? Classify(lastSize)
                    : SigningErrorClass.Healthy; // 4xx — не сбой подписи (напр. валидация документа)

            return new SigningHealth(cls, lastTs, lastCode, lastSize, n200, n500);
        }
        catch
        {
            return SigningHealth.None; // диагностика не должна ронять health-проверку
        }
    }

    private static SigningErrorClass Classify(int size) => size switch
    {
        SizeRsaMismatch => SigningErrorClass.RsaGostMismatch,
        SizeCryptoLib => SigningErrorClass.CryptoLib,
        _ => SigningErrorClass.Unknown500,
    };

    private static string? LatestAccessLog(string logDir)
    {
        try
        {
            return Directory.EnumerateFiles(logDir, "access_log.*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    /// <summary>Прочитать последние <paramref name="tailBytes"/> байт файла как UTF-8, отбросив
    /// первую (вероятно обрезанную) строку. Открываем с ReadWrite-шарингом — лог пишется параллельно.</summary>
    private static string ReadTail(string file, int tailBytes)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long len = fs.Length;
        long start = Math.Max(0, len - tailBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[len - start];
        int read = fs.Read(buf, 0, buf.Length);
        var text = Encoding.UTF8.GetString(buf, 0, read);
        if (start > 0)
        {
            int nl = text.IndexOf('\n');
            if (nl >= 0) text = text[(nl + 1)..];
        }
        return text;
    }
}
