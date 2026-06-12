using PhysicalReleaseGuard.Api;
using PhysicalReleaseGuard.Services;
using PhysicalReleaseGuard.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PhysicalReleaseGuard;

/// <summary>
/// Registers plugin services with the Jellyfin dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ITmdbService, TmdbService>();
        serviceCollection.AddSingleton<IHiddenTagService, HiddenTagService>();
        serviceCollection.AddSingleton<IScheduledTask, HiddenTagScanTask>();
        serviceCollection.AddSingleton<IHostedService, LibraryWatcherService>();
        serviceCollection.AddSingleton<UserTagBlockService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<UserTagBlockService>());
        serviceCollection.AddSingleton<PhysicalReleaseGuardController>();
    }
}
