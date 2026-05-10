using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Jellytriggers.Web;

/// <summary>
/// File Transformation callback. Mutates jellyfin-web's <c>index.html</c> in
/// flight to load our pane bundle.
/// </summary>
/// <remarks>
/// File Transformation invokes this method by reflection (assembly + class +
/// method names baked into the registration payload), passing a JObject
/// whose <c>contents</c> property is the current state of the file. Our job
/// is to swap that string for one with our <c>&lt;script&gt;</c> tag in it.
/// </remarks>
public static class IndexHtmlInjector
{
    private const string Marker = "<!-- jellytriggers:injected -->";
    private const string Snippet =
        "<script defer src=\"/Plugins/Jellytriggers/script.js\"></script>";

    /// <summary>The exact contract File Transformation calls.</summary>
    public static void Transform(JObject payload)
    {
        var current = payload.Value<string>("contents");
        if (string.IsNullOrEmpty(current) || current.Contains(Marker))
        {
            return;
        }

        var injection = "    " + Marker + "\n    " + Snippet + "\n";
        string modified;

        // Prefer injecting just before </body>; fall back to </head> for the
        // edge case of an unusual web client that has no body close tag visible
        // (some custom transformation chains rewrite around that).
        var bodyIdx = current.LastIndexOf("</body>", System.StringComparison.OrdinalIgnoreCase);
        if (bodyIdx >= 0)
        {
            modified = current.Insert(bodyIdx, injection);
        }
        else
        {
            var headIdx = current.LastIndexOf("</head>", System.StringComparison.OrdinalIgnoreCase);
            if (headIdx < 0)
            {
                // No close tag — give up cleanly rather than corrupting the page.
                return;
            }

            modified = current.Insert(headIdx, injection);
        }

        payload["contents"] = modified;
    }
}
