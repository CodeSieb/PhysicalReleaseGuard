using PhysicalReleaseGuard.Api;
using PhysicalReleaseGuard.Services;
using PhysicalReleaseGuard.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PhysicalReleaseGuard;

/// <summary>
/// Registers plugin services with the Jellyfin dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Polymorphic dependencies for the polite TMDb client. Read live config so the
        // bucket rate and retry limits reflect anything saved before the first resolution.
        serviceCollection.AddSingleton<TokenBucket>(_ =>
            new TokenBucket(() => SafeRequestsPerSecond()));

        serviceCollection.AddSingleton<IResilientHttpClient>(sp =>
        {
            var tokenBucket = sp.GetRequiredService<TokenBucket>();
            var logger = sp.GetService<ILogger<ResilientHttpClient>>();
            var settings = new ResilientHttpClientSettings
            {
                MaxRetries = SafeMaxRetries(),
                RetryInitialDelayMs = SafeRetryInitialDelayMs(),
                RequestTimeoutSeconds = 15,
            };
            return new ResilientHttpClient(tokenBucket, settings, logger);
        });

        serviceCollection.AddSingleton<IUnmatchedItemsStore, UnmatchedItemsStore>(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetService<ILogger<UnmatchedItemsStore>>();
            var options = new UnmatchedItemsStoreOptions
            {
                MaxEntries = SafeUnmatchedMaxEntries()
            };
            return new UnmatchedItemsStore(appPaths, options, logger);
        });

        serviceCollection.AddSingleton<ITmdbService, TmdbService>(sp =>
        {
            var http = sp.GetRequiredService<IResilientHttpClient>();
            var logger = sp.GetService<ILogger<TmdbService>>();
            return new TmdbService(http, logger!);
        });

        serviceCollection.AddSingleton<IHiddenTagService, HiddenTagService>(sp =>
        {
            var tmdb = sp.GetRequiredService<ITmdbService>();
            var unmatched = sp.GetRequiredService<IUnmatchedItemsStore>();
            var logger = sp.GetService<ILogger<HiddenTagService>>();
            return new HiddenTagService(tmdb, unmatched, logger!);
        });

        serviceCollection.AddSingleton<IScheduledTask, HiddenTagScanTask>(sp =>
        {
            var libraryManager = sp.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>();
            var hiddenTagService = sp.GetRequiredService<IHiddenTagService>();
            var logger = sp.GetService<ILogger<HiddenTagScanTask>>();
            return new HiddenTagScanTask(libraryManager, hiddenTagService, logger!);
        });

        serviceCollection.AddSingleton<IHostedService, LibraryWatcherService>(sp =>
        {
            var libraryManager = sp.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>();
            var hiddenTagService = sp.GetRequiredService<IHiddenTagService>();
            var logger = sp.GetService<ILogger<LibraryWatcherService>>();
            return new LibraryWatcherService(libraryManager, hiddenTagService, logger!);
        });

        serviceCollection.AddSingleton<UserTagBlockService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<UserTagBlockService>());
        serviceCollection.AddSingleton<PhysicalReleaseGuardController>(sp =>
        {
            var libraryManager = sp.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>();
            var hiddenTagService = sp.GetRequiredService<IHiddenTagService>();
            var tmdbService = sp.GetRequiredService<ITmdbService>();
            var userTagBlockService = sp.GetRequiredService<UserTagBlockService>();
            var unmatchedStore = sp.GetRequiredService<IUnmatchedItemsStore>();
            var logger = sp.GetService<ILogger<PhysicalReleaseGuardController>>();
            return new PhysicalReleaseGuardController(
                libraryManager,
                hiddenTagService,
                tmdbService,
                userTagBlockService,
                unmatchedStore,
                logger!);
        });
    }

    private static int SafeRequestsPerSecond()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return 4;
        }

        return Math.Max(1, config.MaxRequestsPerSecond);
    }

    private static int SafeMaxRetries()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return 3;
        }

        return Math.Max(0, config.MaxRetriesPerItem);
    }

    private static int SafeRetryInitialDelayMs()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return 500;
        }

        return Math.Max(50, config.RetryInitialDelayMs);
    }

    private static int SafeUnmatchedMaxEntries()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return 1000;
        }

        return Math.Max(50, config.UnmatchedItemMaxEntries);
    }
}
