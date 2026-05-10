using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellytriggers.Api.Models;

/// <summary>The exact JSON the controller hands back to the JS pane.</summary>
public sealed class PanePayload
{
    /// <summary>Tells the JS what to render (the trigger list, a "set up your key" prompt, etc.).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PaneState State { get; set; } = PaneState.Ok;

    /// <summary>The resolved DTDD media id (purely informational, but useful for debugging).</summary>
    public int? DtddMediaId { get; set; }

    /// <summary>Filtered to only this user's favorites. May be empty even when <see cref="State"/> is Ok.</summary>
    public IReadOnlyList<PaneItem> Items { get; set; } = System.Array.Empty<PaneItem>();
}

/// <summary>What the JS pane should display.</summary>
public enum PaneState
{
    /// <summary>Normal render. Show <see cref="PanePayload.Items"/>.</summary>
    Ok = 0,

    /// <summary>The viewer hasn't given Jellytriggers their DTDD API key yet.</summary>
    KeyMissing = 1,

    /// <summary>We resolved the item but it's not on doesthedogdie.com.</summary>
    NotOnDoesTheDogDie = 2,

    /// <summary>DTDD has the item, but none of its topics are in this viewer's favorites.</summary>
    UserHasNoFavorites = 3,
}
