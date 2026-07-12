using System.Net.Http;
using System.Text;
using System.Xml.Linq;

namespace UtmOrchestrator.Core.Egais;

/// <summary>
/// Запросы к ЕГАИС через УТМ. Пока — QueryNATTN («ТТН без акта»): просит ЕГАИС
/// прислать список/повтор всех входящих накладных, по которым учётная система ещё не
/// ответила актом. Полезно после переноса УТМ или сбоя, чтобы «дозабрать» всё
/// необработанное (так делают учётные системы). Ответ (ReplyNoAnswerTTN + сами ТТН)
/// приходит в очередь /opt/out.
///
/// Формат подтверждён по схемам самого УТМ (xsd-1.91): элемент QueryNATTN типа
/// QueryParameters, параметры необязательны → пустой запрос = все необработанные.
/// Приём: POST multipart /opt/in/QueryNATTN, поле xml_file. localhost → UseProxy=false
/// (системный прокси ломает обращения к 127.0.0.1).
/// </summary>
public static class UtmQueries
{
    private static readonly XNamespace Ns = "http://fsrar.ru/WEGAIS/WB_DOC_SINGLE_01";

    // Отдельный клиент: прокси в обход (localhost), небольшой таймаут.
    private static readonly HttpClient _http =
        new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public sealed record Result(bool Ok, string? ReplyId, string Message);

    /// <summary>Собрать XML запроса QueryNATTN (все необработанные ТТН) для данного ФСРАР.</summary>
    public static string BuildQueryNATTN(string fsrarId)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Ns + "Documents",
                new XAttribute(XNamespace.Xmlns + "ns", Ns.NamespaceName),
                new XAttribute("Version", "1.0"),
                new XElement(Ns + "Owner",
                    new XElement(Ns + "FSRAR_ID", fsrarId)),
                new XElement(Ns + "Document",
                    // Пустой QueryNATTN = запрос всех ТТН без акта (параметры необязательны).
                    new XElement(Ns + "QueryNATTN"))));
        return doc.Declaration + "\n" + doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Отправить QueryNATTN в УТМ на порту port от имени fsrarId.</summary>
    public static async Task<Result> RequestUnprocessedAsync(int port, string fsrarId, CancellationToken ct = default)
    {
        if (port <= 0) return new(false, null, "у УТМ нет порта");
        if (string.IsNullOrWhiteSpace(fsrarId)) return new(false, null, "неизвестен ФСРАР УТМ");

        string xml = BuildQueryNATTN(fsrarId);
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes(xml));
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
            form.Add(file, "xml_file", "QueryNATTN.xml");

            string url = $"http://127.0.0.1:{port}/opt/in/QueryNATTN";
            using var resp = await _http.PostAsync(url, form, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return new(false, null, $"УТМ ответил {(int)resp.StatusCode}: {Trim(body)}");

            // Успех: УТМ возвращает XML со ссылкой на подписанный документ и replyId.
            string? replyId = TryReadReplyId(body);
            return new(true, replyId, replyId is null ? "запрос принят УТМ" : $"запрос принят (replyId {replyId})");
        }
        catch (Exception e)
        {
            return new(false, null, "не удалось отправить запрос: " + e.Message);
        }
    }

    private static string? TryReadReplyId(string body)
    {
        try
        {
            var d = XDocument.Parse(body);
            // Обычно <url replyId="...">...</url> — забираем атрибут replyId.
            var el = d.Descendants().FirstOrDefault(x => x.Attribute("replyId") is not null);
            return el?.Attribute("replyId")?.Value;
        }
        catch { return null; }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
