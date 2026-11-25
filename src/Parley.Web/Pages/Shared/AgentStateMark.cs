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
