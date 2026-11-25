using Parley.Client.Views;

namespace Parley.Web.Pages.Shared;

/// <summary>An agent's state as the agents list draws it: its mark, and the words the mark stands for.</summary>
/// <remarks>
/// Feature 042, the person's feedback: the list's state column ran as sentences of different lengths that did not line up.
/// Each state is now a mark in a round badge, the words its tooltip and a screen reader's text. The mark is the state's own
/// (<see cref="AgentNow.Mark"/>), never read back out of its wording, so rewording a state cannot change its badge.
/// </remarks>
public static class AgentStateMark
{
    /// <summary>What the list draws for an agent: its mark, and its words without the mark they may begin with.</summary>
    /// <param name="now">What the list says about the agent.</param>
    /// <returns>The mark, and the words.</returns>
    public static (string Mark, string Words) Split(AgentNow now)
    {
        ArgumentNullException.ThrowIfNull(now);

        var words = now.Words.StartsWith(now.Mark, StringComparison.Ordinal) ? now.Words[now.Mark.Length..].TrimStart() : now.Words;

        return (now.Mark, words);
    }

    /// <summary>Every mark the console draws, each with what it means, in the legend's order.</summary>
    /// <remarks>Read once from the console's own legend (<see cref="Activities.Legend"/>), so a page never parses it.</remarks>
    public static IReadOnlyList<(string Mark, string Means)> Legend { get; } =
        [.. Activities.Legend.Select(entry => entry.Split(' ', 2)).Select(parts => (parts[0], parts[1]))];

    /// <summary>Where a mark comes in the legend, so the states' sections keep one order whoever is in them.</summary>
    /// <param name="mark">A state's mark.</param>
    /// <returns>Its place, or after every mark the legend names.</returns>
    public static int Order(string mark)
    {
        for (var i = 0; i < Legend.Count; i++)
        {
            if (Legend[i].Mark == mark) { return i; }
        }

        return Legend.Count;
    }

    /// <summary>The CSS class that tints a mark's badge; a state that needs no attention is left plain.</summary>
    /// <param name="mark">The state's mark.</param>
    /// <returns>A class name, or an empty string for a plain badge.</returns>
    public static string Tint(string mark) => mark switch
    {
        Activities.WorkingMark => "state-working",
        Activities.BackgroundMark => "state-background",
        Activities.StoppedMark => "state-stopped",
        Activities.OutsideMark => "state-outside",
        _ => string.Empty,
    };
}
