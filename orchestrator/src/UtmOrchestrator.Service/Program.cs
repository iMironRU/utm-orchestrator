using Microsoft.Extensions.Hosting.WindowsServices;
using UtmOrchestrator.Service;

var builder = Host.CreateApplicationBuilder(args);

// Работает и как Windows-служба, и как обычная консоль (для обкатки).
builder.Services.AddWindowsService(options => options.ServiceName = "UtmOrchestrator");
builder.Services.AddHostedService<HealthWorker>();

var host = builder.Build();
host.Run();
