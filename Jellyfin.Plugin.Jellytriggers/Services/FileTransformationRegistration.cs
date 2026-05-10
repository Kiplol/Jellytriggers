using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellytriggers.Web;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Jellytriggers.Services;

/// <summary>
/// Injects our script tag into Jellyfin's <c>index.html</c> on startup via direct disk patch.
/// </summary>
/// <remarks>
/// Uses <see cref="IApplicationPaths.WebPath"/> — Jellyfin's own interface for locating the
/// jellyfin-web directory. This is correct in all Jellyfin deployments including Docker, where
/// <c>IWebHostEnvironment.WebRootPath</c> is empty because Jellyfin configures static files
/// via a custom file provider, not the standard ASP.NET web root.
///
/// We do NOT register with File Transformation. FT 2.5.9.0 persists registrations to disk and
/// replays them across restarts. Its internal <c>IServiceProvider</c> is disposed after startup,
/// so any invocation of a registered callback throws <see cref="ObjectDisposedException"/> on
/// every <c>GET /web/</c> request. The direct disk patch is the correct injection mechanism.
/// </remarks>
public sealed class FileTransformationRegistration : IHostedService
{
    private readonly ILogger<FileTransformationRegistration> _logger;
    private readonly IApplicationPaths _appPaths;

    public FileTransformationRegistration(
        ILogger<FileTransformationRegistration> logger,
        IApplicationPaths appPaths)
    {
        _logger = logger;
        _appPaths = appPaths;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            PatchIndexHtmlDirectly();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellytriggers: direct index.html patch failed.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void PatchIndexHtmlDirectly()
    {
        var webPath = _appPaths.WebPath;
        if (string.IsNullOrEmpty(webPath))
        {
            _logger.LogWarning("Jellytriggers: IApplicationPaths.WebPath is empty; cannot locate index.html.");
            return;
        }

        var indexPath = Path.Combine(webPath, "index.html");
        if (!File.Exists(indexPath))
        {
            _logger.LogWarning("Jellytriggers: index.html not found at {Path}.", indexPath);
            return;
        }

        var original = File.ReadAllText(indexPath);
        var patched = IndexHtmlInjector.Transform(new JObject { ["contents"] = original });

        if (string.Equals(patched, original, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Jellytriggers: index.html already contains current injection — no change needed.");
            return;
        }

        File.WriteAllText(indexPath, patched);
        _logger.LogInformation("Jellytriggers: patched index.html at {Path}.", indexPath);
    }
}
