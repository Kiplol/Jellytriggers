using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Jellytriggers.Services;

/// <summary>
/// Registers our <see cref="Web.IndexHtmlInjector"/> with the
/// <c>jellyfin-plugin-file-transformation</c> plugin on startup, so the
/// pane's script tag appears in the served <c>index.html</c> automatically.
/// </summary>
/// <remarks>
/// File Transformation can't be referenced as a normal NuGet/library — its
/// author calls this out in the README — so we discover it at runtime via
/// <see cref="AssemblyLoadContext.All"/> and invoke its <c>PluginInterface
/// .RegisterTransformation</c> by reflection. If the plugin isn't installed,
/// we log a friendly note and bail out; users still have the manual script
/// tag fallback documented on the admin page.
/// </remarks>
public sealed class FileTransformationRegistration : IHostedService
{
    // A stable Guid so the FT plugin treats repeat registrations as updates,
    // not duplicates. Don't change this casually.
    private static readonly Guid TransformationId =
        Guid.Parse("c1f6d2a8-43e1-4b30-8a8a-9f1cf7b6f5e1");

    private readonly ILogger<FileTransformationRegistration> _logger;

    public FileTransformationRegistration(ILogger<FileTransformationRegistration> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            TryRegister();
        }
        catch (Exception ex)
        {
            // Never let a registration miss bring down plugin startup.
            _logger.LogWarning(ex, "Jellytriggers: failed to register File Transformation hook.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void TryRegister()
    {
        var fileTransformation = AssemblyLoadContext.All
            .SelectMany(ctx => ctx.Assemblies)
            .FirstOrDefault(asm => asm.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) == true);

        if (fileTransformation == null)
        {
            _logger.LogInformation(
                "Jellytriggers: File Transformation plugin not detected. Falling back to the manual script-tag install path.");
            return;
        }

        var pluginInterface = fileTransformation
            .GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        if (pluginInterface == null)
        {
            _logger.LogWarning(
                "Jellytriggers: File Transformation is loaded but its PluginInterface type wasn't found. Plugin version may have changed; please file an issue.");
            return;
        }

        var register = pluginInterface.GetMethod(
            "RegisterTransformation",
            BindingFlags.Public | BindingFlags.Static);
        if (register == null)
        {
            _logger.LogWarning(
                "Jellytriggers: File Transformation is loaded but RegisterTransformation method wasn't found.");
            return;
        }

        var ourAssemblyName = typeof(Web.IndexHtmlInjector).Assembly.FullName!;
        var payload = new JObject
        {
            ["id"] = TransformationId.ToString("D"),
            ["fileNamePattern"] = "index\\.html$",
            ["callbackAssembly"] = ourAssemblyName,
            ["callbackClass"] = typeof(Web.IndexHtmlInjector).FullName,
            ["callbackMethod"] = nameof(Web.IndexHtmlInjector.Transform),
        };

        register.Invoke(null, new object?[] { payload });
        _logger.LogInformation("Jellytriggers: registered index.html script injection with File Transformation.");
    }
}
