using System.Text;
using UtmOrchestrator.Core.Tokens;

Console.OutputEncoding = Encoding.UTF8;

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "scan";

switch (command)
{
    case "scan":
        ScanTokens();
        break;
    default:
        Console.WriteLine($"Неизвестная команда: {command}");
        Console.WriteLine("Доступно: scan");
        break;
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
