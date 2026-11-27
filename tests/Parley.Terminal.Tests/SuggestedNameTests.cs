using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the conversation name the terminal offers is one the broker takes, and carries the surface's current name.
/// </summary>
/// <remarks>
/// A suggestion is pre-filled into the composer and sent as it stands by anybody who does not change it, so a name the
/// rule refuses would be a form that fails on its default. It said `top-` until the gate over the rename found the old
/// name still being written into conversations.
/// </remarks>
[Unit]
public sealed class SuggestedNameTests
{
    [Fact]
    public void The_suggestion_is_a_name_the_rule_admits()
    {
        var suggested = Participation.Suggested();

        Assert.Null(Participation.ConversationProblem(suggested));
    }

    [Fact]
    public void The_suggestion_says_which_surface_started_it()
    {
        Assert.StartsWith("terminal-", Participation.Suggested(), StringComparison.Ordinal);
    }
}
