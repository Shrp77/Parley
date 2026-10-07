using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Parley.Mcp;

/// <summary>
/// One item of a roadmap an agent proposes (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three fields and no fourth.</b> What the item is, who it is for where that is known, and which earlier items it
/// needs. There is nowhere to put background, reasons or narrative, and that is the point: the shape itself discourages
/// the paragraphs a free-text list would invite, and detail belongs in the item's own conversation once a person starts
/// it. It is guidance in the tool's description and not a limit in storage, because a person reads the proposal and is
/// the control (constitution 6.0.0).
/// </para>
/// </remarks>
public sealed class RoadmapItemInput
{
    /// <summary>Gets or sets what is to be done: one line, led by a verb.</summary>
    [JsonPropertyName("text")]
    [Description("One line, verb first, naming the thing to be done. No background, no reasons.")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets who the item is meant for, where that is known.</summary>
    [JsonPropertyName("for")]
    [Description("Optional. The whole agent name it is meant for, as parley_agents lists it. Leave out if you do not know.")]
    public string? For { get; set; }

    /// <summary>Gets or sets the earlier items this one needs done first.</summary>
    [JsonPropertyName("needs")]
    [Description("Optional. The item numbers (1 is the first) that must be done before this one. Each must be earlier in the list.")]
    public int[]? Needs { get; set; }
}
