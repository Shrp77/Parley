using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the node marks an instruction before it becomes a turn.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §11.5 requires an instruction to reach the agent marked as the operator's rather than
/// as a peer's. What that section is protecting is the agent's ability to tell which of the two
/// it is reading — and an instruction delivered as bare text is worse than unlabelled, because it
/// is indistinguishable from the agent's own context. An agent that cannot tell it was instructed
/// cannot exercise the refusal the same section grants it.
/// </para>
/// <para>
/// Checked at the call site rather than through a turn, because the defect this catches is one
/// character wide: passing the text instead of the marked text. Everything downstream of that
/// point behaves identically either way, which is exactly why it went unnoticed — the instruction
/// arrived, the agent answered, the outcome was reported, and nothing anywhere was wrong except
/// that the agent had no idea who was asking.
/// </para>
/// </remarks>
public sealed class InstructionMarkingTests
{
    private static string NodeCommandSource =>
        File.ReadAllText(RepositoryPath("src/Parley.Cli/NodeCommand.cs"));

    [Fact]
    public void The_operators_text_is_marked_before_it_is_given_as_a_turn()
    {
        Assert.Contains(
            "OperatorInstruction.Mark(instruct.Text)",
            NodeCommandSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_bare_text_never_reaches_a_turn()
    {
        // The half that keeps the check above from passing beside a second, unmarked path. A node
        // that marked in one branch and not another would satisfy a test that only looked for the
        // marking.
        Assert.DoesNotContain(
            "TakeTurnAsync(instruct.Text", NodeCommandSource, StringComparison.Ordinal);
    }

    /// <summary>Finds a path in the repository from wherever the tests run.</summary>
    /// <param name="relative">The path, relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 }
               && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
