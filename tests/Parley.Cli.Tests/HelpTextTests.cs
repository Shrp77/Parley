using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What the usage text promises about the destructive commands.
/// </summary>
/// <remarks>
/// <para>
/// Written because the help drifted and nothing noticed. When purge refused any agent that had
/// traded, describing it as "delete a registration that never traded" was accurate and reassuring
/// in equal measure. When purge changed to destroy the agent and every conversation it was party
/// to, that line stayed exactly as it was — so the most destructive command in the tool was
/// documented, to the operator's face, as the narrow and safe one.
/// </para>
/// <para>
/// That is a worse failure than an undocumented command. Somebody reading <c>--help</c> before
/// running an irreversible operation is doing precisely the right thing, and the tool was
/// answering them wrongly.
/// </para>
/// <para>
/// Checked against the source rather than by running the binary, because the usage text lives in
/// a file of top-level statements and is not reachable as API. That is a weaker check than
/// invoking it and reading the output, and it is the strongest one available without restructuring
/// the entry point around being testable.
/// </para>
/// </remarks>
public sealed class HelpTextTests
{
    private static string Usage => RepositoryFile.Read("src/Parley.Cli/Program.cs");

    [Fact]
    public void The_usage_text_does_not_describe_purge_as_narrow()
    {
        // The exact wording that went stale, kept as the assertion so the drift cannot recur in
        // its original form. A future rewording that reintroduces the old promise by other words
        // will not be caught here — the test below is the one that carries the intent.
        Assert.DoesNotContain(
            "delete a registration that never traded", Usage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Purge_is_described_as_destructive()
    {
        // An operator scanning the usage list should be able to tell the two registry commands
        // apart on severity without running either. retire withdraws; purge destroys.
        var line = Usage
            .Split('\n')
            .Single(l => l.Contains("] purge <agent>", StringComparison.Ordinal));

        Assert.Contains("DESTROY", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Retire_is_not_described_as_destructive()
    {
        // The other half, and the reason the check above is worth anything: if both lines shouted
        // the operator learns to ignore both, and the pair stops distinguishing what it exists to
        // distinguish.
        var line = Usage
            .Split('\n')
            .Single(l => l.Contains("] retire <agent>", StringComparison.Ordinal));

        Assert.DoesNotContain("DESTROY", line, StringComparison.Ordinal);
    }
}
