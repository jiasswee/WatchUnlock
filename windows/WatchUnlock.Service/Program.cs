using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WatchUnlock.Service;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args, ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddWindowsService(options => options.ServiceName = "WatchUnlock");
builder.Services.AddSingleton<ServiceSettingsStore>();
builder.Services.AddSingleton<BleCoordinator>();
builder.Services.AddHostedService<ServiceWorker>();
await builder.Build().RunAsync();
