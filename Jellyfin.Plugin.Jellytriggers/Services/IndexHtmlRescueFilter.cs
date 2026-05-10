using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellytriggers.Web;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Jellytriggers.Services;

/// <summary>
/// Registers <see cref="IndexHtmlRescueMiddleware"/> at the front of the
/// ASP.NET pipeline so it runs before Jellyfin's ExceptionMiddleware.
/// </summary>
public sealed class IndexHtmlRescueFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<IndexHtmlRescueMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Catches <see cref="ObjectDisposedException"/> thrown by File Transformation
/// 2.5.9.0 when it tries to serve <c>index.html</c> with a stale (disposed)
/// <c>IServiceProvider</c>, and falls back to serving the file directly from
/// disk (already patched by <see cref="FileTransformationRegistration"/>).
/// </summary>
/// <remarks>
/// FT persists registration data across restarts. If a prior registration's
/// callback was never successfully cached (due to bugs in earlier plugin
/// versions), FT replays the stale registration on every request and crashes.
/// This middleware makes Jellyfin resilient to that crash — the user sees a
/// working page regardless of FT's internal state.
/// </remarks>
public sealed class IndexHtmlRescueMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<IndexHtmlRescueMiddleware> _logger;

    public IndexHtmlRescueMiddleware(
        RequestDelegate next,
        IApplicationPaths appPaths,
        ILogger<IndexHtmlRescueMiddleware> logger)
    {
        _next = next;
        _appPaths = appPaths;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        bool isIndexHtml = path is "/web/" or "/web" or "/web/index.html";

        if (!isIndexHtml)
        {
            await _next(context);
            return;
        }

        try
        {
            await _next(context);
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogWarning(
                ex,
                "Jellytriggers: File Transformation crashed serving index.html (disposed IServiceProvider). " +
                "Serving from disk directly.");

            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                await ServeDiskIndexHtml(context);
            }
        }
    }

    private async Task ServeDiskIndexHtml(HttpContext context)
    {
        var webPath = _appPaths.WebPath;
        if (string.IsNullOrEmpty(webPath))
        {
            _logger.LogWarning("Jellytriggers: WebPath is empty; cannot serve fallback index.html.");
            context.Response.StatusCode = 500;
            return;
        }

        var indexPath = Path.Combine(webPath, "index.html");
        if (!File.Exists(indexPath))
        {
            _logger.LogWarning("Jellytriggers: fallback index.html not found at {Path}.", indexPath);
            context.Response.StatusCode = 500;
            return;
        }

        var content = await File.ReadAllTextAsync(indexPath, context.RequestAborted);

        // Ensure our script tag is present (disk patch may have already done this,
        // but we guarantee it here in case the patch hasn't run yet).
        var patched = IndexHtmlInjector.Transform(new JObject { ["contents"] = content });

        var bytes = Encoding.UTF8.GetBytes(patched);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);

        _logger.LogInformation("Jellytriggers: served fallback index.html ({Bytes} bytes).", bytes.Length);
    }
}
