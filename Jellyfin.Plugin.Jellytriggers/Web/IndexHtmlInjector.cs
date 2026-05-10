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
///
/// IMPORTANT: FT may cache or write the transformed file to disk. We must NOT
/// short-circuit on the marker alone — we verify the exact versioned URL is
/// present. On version upgrades the old injection is stripped and replaced.
/// </remarks>
public static class IndexHtmlInjector
{
    private const string Marker = "<!-- jellytriggers:injected -->";

    /// <summary>The exact contract File Transformation calls.</summary>
    public static string Transform(JObject payload)
    {
        try
        {
            var current = payload?.Value<string>("contents") ?? string.Empty;
            if (string.IsNullOrEmpty(current))
            {
                return current;
            }

            // Embed the assembly version in the URL so any caching proxy sees a
            // new URL on each release and cannot serve a stale cached script.
            var version = typeof(IndexHtmlInjector).Assembly.GetName().Version?.ToString() ?? "0";
            var snippet = $"<script defer src=\"/Plugins/Jellytriggers/script.js?v={version}\"></script>";

            // Already injected with THIS exact version — nothing to do.
            if (current.Contains(snippet))
            {
                return current;
            }

            // A previous version's injection is present (different version or a
            // pre-versioned unversioned URL). Strip it before re-injecting so we
            // don't accumulate stale script tags across plugin updates.
            if (current.Contains(Marker))
            {
                current = StripPreviousInjection(current);
            }

            var injection = "    " + Marker + "\n    " + snippet + "\n";

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

    /// <summary>
    /// Removes a previous Jellytriggers injection block (marker + script line)
    /// so the caller can insert the current-version snippet in its place.
    /// </summary>
    private static string StripPreviousInjection(string html)
    {
        var markerIdx = html.IndexOf(Marker, System.StringComparison.Ordinal);
        if (markerIdx < 0)
        {
            return html;
        }

        // Walk back to include any leading whitespace on the marker line and
        // the preceding newline, so we don't leave a blank line behind.
        var blockStart = markerIdx;
        while (blockStart > 0 && (html[blockStart - 1] == ' ' || html[blockStart - 1] == '\t'))
        {
            blockStart--;
        }

        if (blockStart > 0 && html[blockStart - 1] == '\n')
        {
            blockStart--;
        }

        // Walk forward past the marker line and the script tag line.
        var afterMarkerLine = html.IndexOf('\n', markerIdx);
        if (afterMarkerLine < 0)
        {
            return html.Substring(0, blockStart);
        }

        var afterScriptLine = html.IndexOf('\n', afterMarkerLine + 1);
        var blockEnd = afterScriptLine >= 0 ? afterScriptLine : html.Length;

        return html.Substring(0, blockStart) + html.Substring(blockEnd);
    }
}
