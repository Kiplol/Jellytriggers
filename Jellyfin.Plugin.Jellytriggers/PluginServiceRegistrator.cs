using Jellyfin.Plugin.Jellytriggers.Api;
using Jellyfin.Plugin.Jellytriggers.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Jellytriggers;

/// <summary>
/// Hooks into Jellyfin's DI container at startup so our services and
/// hosted background workers get the standard wiring.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost serverApplicationHost)
    {
        // Stateful services (hold caches in memory + on disk) — singletons.
        serviceCollection.AddSingleton<UserKeyStore>();
        serviceCollection.AddSingleton<TriggerCache>();

        // Stateless typed HTTP client — fine as transient since IHttpClientFactory
        // handles the connection pooling for us.
        serviceCollection.AddTransient<DoesTheDogDieClient>();

        // Lookup service composes the client + cache. Transient is fine because
        // it carries no state of its own.
        serviceCollection.AddTransient<TriggerLookupService>();

        // Background worker that patches index.html on disk at startup.
        serviceCollection.AddHostedService<FileTransformationRegistration>();

        // Rescue middleware: if File Transformation crashes serving /web/
        // (due to its disposed-IServiceProvider bug), catch the exception and
        // serve index.html directly from disk so Jellyfin stays functional.
        serviceCollection.AddSingleton<IndexHtmlRescueMiddleware>();
        serviceCollection.AddSingleton<IStartupFilter, IndexHtmlRescueFilter>();
    }
}
