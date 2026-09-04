using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That `parley top` refuses a terminal it cannot draw on, and says what to run instead.
/// </summary>
/// <remarks>
/// <para>
/// FR-018. A full-screen surface redraws by rewriting lines, so a redirected stdout does not
/// produce a slightly worse experience — it produces thousands of frames in somebody's log file,
/// and a prompt on a redirected stdin either blocks forever or reads whatever the pipe holds.
/// </para>
/// <para>
/// <b>The refusal is checked end to end, and the redirection is real.</b>
/// <c>ToolProcess.Run</c> redirects both pipes in order to read them, which is exactly the
/// condition this refuses — so the probe does not simulate a non-interactive terminal, it runs
/// in one. A test that set a flag instead would prove the branch compiles.
/// </para>
/// <para>
/// It names the commands that do work in a pipe rather than only declining. A refusal that says
/// "this needs a terminal" to somebody's script leaves them with a decision and no options, and
/// SPEC.md §7.3's rule about refusals returning work rather than a retry instruction is the same
/// idea one layer down.
/// </para>
/// </remarks>
public sealed class NonInteractiveTests
{
    [Fact]
    public void Top_refuses_when_there_is_no_terminal_to_draw_on()
    {
        var (exitCode, error) = ToolProcess.Run("top");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("redirected", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("parley project list")]
    [InlineData("parley registry")]
    [InlineData("parley console")]
    public void The_refusal_names_something_that_does_work_in_a_pipe(string alternative)
    {
        // Each of these answers some part of what `top` shows and all of them work redirected.
        // Naming them is what makes this a refusal somebody can act on rather than a wall.
        var (_, error) = ToolProcess.Run("top");

        Assert.Contains(alternative, error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_refusal_draws_nothing()
    {
        // The failure this exists to prevent, stated as an assertion: not one frame, not a
        // partial table, nothing that would be a screenful of control characters in a log. A
        // surface that drew its banner before checking would pass every test above and still be
        // the bug.
        var (_, error) = ToolProcess.Run("top");

        Assert.DoesNotContain("[", error, StringComparison.Ordinal);
        Assert.DoesNotContain("─", error, StringComparison.Ordinal);
        Assert.DoesNotContain("│", error, StringComparison.Ordinal);
    }
}
