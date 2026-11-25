using Jellyfin.Plugin.ArcfinSync.Api.Integrations;
using Jellyfin.Plugin.ArcfinSync.Api.Limits;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ArcfinSync;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(SeerrClient.HttpClientName);
        serviceCollection.AddHostedService<WatchLimitService>();
        serviceCollection.AddHostedService<SeerrQueueService>();
        serviceCollection.AddHostedService<Jellyfin.Plugin.ArcfinSync.Api.Security.SecurityService>();
        serviceCollection.AddHostedService<Jellyfin.Plugin.ArcfinSync.Api.Household.SharedWatchService>();
        serviceCollection.AddHostedService<Jellyfin.Plugin.ArcfinSync.Api.Notifications.SeerrWatchService>();
        serviceCollection.AddHostedService<Jellyfin.Plugin.ArcfinSync.Api.Notifications.LiveNudgeService>();
    }
}
