using Parley.Client.Views;

namespace Parley.Web.Pages.Shared;

/// <summary>An agent's state as the agents list draws it: its mark, and the words the mark stands for.</summary>
/// <remarks>
/// Feature 042, the person's feedback: the list's state column ran as sentences of different lengths that did not line up.
/// Each state is now a mark in a round badge, the words its tooltip and a screen reader's text. The marks are the ones the
/// whole console uses (<see cref="Activities"/>), so the list, the room and the legend agree; words that come without one
/// are given the mark of the state they describe.
/// </remarks>
public static class AgentStateMark
{
    private static readonly string[] Marks =
        [Activities.WorkingMark, Activities.RunningMark, Activities.StoppedMark, Activities.NotRunningMark, Activities.OutsideMark, Activities.BackgroundMark];

    /// <summary>Splits what the list says about an agent into its mark and its words.</summary>
    /// <param name="words">What the list says, for example <c>◆ working &lt;1m</c> or <c>startable</c>.</param>
    /// <returns>The mark, and the words without it.</returns>
    public static (string Mark, string Words) Split(string words)
    {
        ArgumentNullException.ThrowIfNull(words);

        foreach (var mark in Marks)
        {
            if (words.StartsWith(mark, StringComparison.Ordinal))
            {
                return (mark, words[mark.Length..].TrimStart());
            }
        }

        return (words.Contains("outside", StringComparison.OrdinalIgnoreCase) ? Activities.OutsideMark : Activities.NotRunningMark, words);
    }

    /// <summary>The CSS class that tints a mark's badge.</summary>
    /// <param name="mark">A mark <see cref="Split"/> returned.</param>
    /// <returns>A class name.</returns>
    public static string Tint(string mark) => mark switch
    {
        Activities.WorkingMark => "state-working",
        Activities.BackgroundMark => "state-background",
        Activities.StoppedMark => "state-stopped",
        Activities.OutsideMark => "state-outside",
        Activities.RunningMark => "state-running",
        _ => "state-idle",
    };
}
