namespace Jellyfin.Plugin.Jellytriggers.Api.Models;

/// <summary>
/// One row in the rendered pane. Slim by design — only what the JS bundle
/// needs to draw a single trigger entry.
/// </summary>
public sealed class PaneItem
{
    public int TopicId { get; set; }

    /// <summary>"Does the dog die", "Does a cat die", etc.</summary>
    public string DoesName { get; set; } = string.Empty;

    public int YesSum { get; set; }

    public int NoSum { get; set; }

    public int NumComments { get; set; }

    /// <summary>Used to deep-link to DTDD: <c>doesthedogdie.com/media/{id}#{slug}</c>.</summary>
    public string? Slug { get; set; }

    public bool Paywalled { get; set; }

    /// <summary>Wording used when noSum > yesSum, e.g. "no dogs die".</summary>
    public string? NotName { get; set; }

    /// <summary>Optional richer "survives" wording, e.g. "the dog survives".</summary>
    public string? SurvivesName { get; set; }
}
