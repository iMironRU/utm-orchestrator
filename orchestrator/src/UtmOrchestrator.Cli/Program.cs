using System.Text;
using UtmOrchestrator.Core.Diagnostics;
using UtmOrchestrator.Core.Discovery;
using UtmOrchestrator.Core.Readers;
using UtmOrchestrator.Core.Services;
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
    case "status":
        await ShowStatus();
        break;
    case "discover":
        await Discover();
        break;
    default:
        Console.WriteLine($"Неизвестная команда: {command}");
        Console.WriteLine("Доступно: scan, readers, status, discover");
        break;
}

static async Task Discover()
{
    var instances = await UtmDiscovery.DiscoverAsync();
    Console.WriteLine($"Найдено УТМ (служб Transport*): {instances.Count}");
    foreach (var i in instances)
    {
        Console.WriteLine(
            $"  {i.ServiceName,-11} порт={i.Port,-5} fsrar={i.ExpectedFsrar ?? "-",-14} serial={i.TokenSerial ?? "-",-10} папка={i.FolderPath}");
    }
}

// Стандартная раскладка 2UTM: 8080=Transport(base), 8081=Transport2, ... 8085=Transport6.
static (int Port, string Service)[] StandardLayout() => new[]
{
    (8080, "Transport"),
    (8081, "Transport2"),
    (8082, "Transport3"),
    (8083, "Transport4"),
    (8084, "Transport5"),
    (8085, "Transport6"),
};

static async Task ShowStatus()
{
    using var http = new UtmHttpClient();
    Console.WriteLine("порт  служба       состояние    ownerId         rsa      gost");
    foreach (var (port, service) in StandardLayout())
    {
        var state = ServiceControl.GetState(service);
        var info = await http.GetInfoAsync(port);
        string own = info?.OwnerId ?? "-";
        string rsa = info == null ? "нет ответа" : (info.RsaOk ? "ok" : "ОШИБКА");
        string gost = info?.GostValid == true ? "ok" : "-";
        Console.WriteLine($"{port}  {service,-11}  {state,-11}  {own,-14}  {rsa,-8}  {gost}");
    }
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
