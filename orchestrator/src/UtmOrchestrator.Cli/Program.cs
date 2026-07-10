using System.Text;
using UtmOrchestrator.Core.Readers;
using UtmOrchestrator.Core.Tokens;

Console.OutputEncoding = Encoding.UTF8;

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "scan";

switch (command)
{
    case "scan":
        ScanTokens();
        break;
    case "readers":
        ListReaders();
        break;
    default:
        Console.WriteLine($"Неизвестная команда: {command}");
        Console.WriteLine("Доступно: scan, readers");
        break;
}

static void ListReaders()
{
    using var table = new ReaderTable();
    var readers = table.ListReaders();
    Console.WriteLine($"PC/SC ридеров: {readers.Count}");
    foreach (var r in readers)
    {
        string device;
        try { device = table.GetDeviceSystemName(r); }
        catch (Exception e) { device = $"<ошибка: {e.Message}>"; }
        Console.WriteLine($"  '{r}'  -> устройство '{device}'");
    }
}

static void ScanTokens()
{
    var scanner = new TokenScanner();
    var tokens = scanner.Scan();
    Console.WriteLine($"Найдено токенов: {tokens.Count}");
    foreach (var t in tokens)
    {
        Console.WriteLine(
            $"  slot {t.SlotId}: serial={t.Serial}  fsrar={(t.HasFsrar ? t.FsrarId : "<нет>")}  reader='{t.ReaderName}'");
    }
}
