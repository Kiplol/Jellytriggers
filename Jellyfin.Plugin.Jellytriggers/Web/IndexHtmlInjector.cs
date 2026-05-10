using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Jellytriggers.Web;

/// <summary>
/// File Transformation callback. Mutates jellyfin-web's <c>index.html</c> in
/// flight to load our pane bundle.
/// </summary>
/// <remarks>
/// FT invokes this by reflection. It does <c>obj.ToObject(parameterType)</c>
/// to build the argument and casts the <em>return value</em> to string — the
/// method must return the (possibly modified) HTML, not mutate the payload.
/// Using <see cref="JObject"/> as the parameter type keeps everything in the
/// same Newtonsoft assembly context as FT itself, avoiding the
/// cross-AssemblyLoadContext deserialization failure that a custom DTO causes.
/// </remarks>
public static class IndexHtmlInjector
{
    private const string Marker = "<!-- jellytriggers:injected -->";
    private const string Snippet =
        "<script defer src=\"/Plugins/Jellytriggers/script.js\"></script>";

    /// <summary>The exact contract File Transformation calls.</summary>
    public static string Transform(JObject payload)
    {
        try
        {
            var current = payload?.Value<string>("contents") ?? string.Empty;

            if (string.IsNullOrEmpty(current) || current.Contains(Marker))
            {
                return current;
            }

            var injection = "    " + Marker + "\n    " + Snippet + "\n";

            // Prefer injecting just before </body>; fall back to </head>.
            var bodyIdx = current.LastIndexOf("</body>", System.StringComparison.OrdinalIgnoreCase);
            if (bodyIdx >= 0)
            {
                return current.Insert(bodyIdx, injection);
            }

            var headIdx = current.LastIndexOf("</head>", System.StringComparison.OrdinalIgnoreCase);
            if (headIdx < 0)
            {
                return current;
            }

            return current.Insert(headIdx, injection);
        }
        catch
        {
            // Never let a transform failure break page serving.
            return payload?.Value<string>("contents") ?? string.Empty;
        }
    }
}
