using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That `parley terminal` reaches the tool that provides it, and says something useful when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// The terminal is a separate package now, so `parley terminal` is a command whose implementation may
/// simply not be on the machine. That is a new failure mode and it is the one a person will meet
/// first, on any machine that installed the broker alone.
/// </para>
/// <para>
/// <b>Checked both ways, because only one of them is the interesting one.</b> Forwarding working
/// is easy to believe and easy to test; the case worth writing down is a missing tool, where the
/// tempting answer — "unknown command" — is untrue and sends somebody to re-read the manual for a
/// command that exists.
/// </para>
/// </remarks>
public sealed class ForwardingTests
{
    [Fact]
    public async Task A_tool_that_is_not_there_is_named_along_with_how_to_get_it()
    {
        var complained = new StringWriter();

        var code = await Forwarding.RunAsync(
            "parley-nothing-provides-this",
            "Parley.Nothing",
            "parley nothing",
            [],
            complained);

        var said = complained.ToString();

        Assert.NotEqual(0, code);

        // The three facts somebody needs, and the one they must not be told.
        Assert.Contains("parley nothing", said, StringComparison.Ordinal);
        Assert.Contains("parley-nothing-provides-this", said, StringComparison.Ordinal);
        Assert.Contains("dotnet tool install --global Parley.Nothing", said, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown command", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task What_the_tool_returns_is_what_the_wrapper_returns()
    {
        // Forwarded to something every platform has, asked for an exit code that is not zero and
        // not the wrapper's own. A wrapper that swallowed the code would report success for a
        // terminal that refused, which is the failure a script would act on.
        var complained = new StringWriter();

        var (tool, arguments) = OperatingSystem.IsWindows()
            ? ("cmd", (string[])["/c", "exit 3"])
            : ("sh", ["-c", "exit 3"]);

        var code = await Forwarding.RunAsync(
            tool, "Nothing.To.Install", "parley test", arguments, complained);

        Assert.Equal(3, code);
        Assert.Equal(string.Empty, complained.ToString());
    }

    [Fact]
    public void The_wrapper_forwards_the_command_line_rather_than_understanding_it()
    {
        // The property, read off the source: `terminal` dispatches into Forwarding and touches
        // nothing it was given except to drop the verb. A branch that read `--config` here would be
        // a second parser to keep in step with the tool it forwards to, which is the thing the
        // split exists to stop.
        var program = RepositoryFile.Read("src/Parley.Cli/Program.cs");
        var block = program[program.IndexOf("if (verb == \"terminal\")", StringComparison.Ordinal)..];

        block = block[..block.IndexOf("\n}", StringComparison.Ordinal)];

        Assert.Contains("Forwarding", block, StringComparison.Ordinal);
        Assert.Contains("forwarded.Remove(\"terminal\")", block, StringComparison.Ordinal);
        Assert.Contains("\"parley-terminal\", \"Parley.Terminal\"", block, StringComparison.Ordinal);
        Assert.DoesNotContain("manifestPath", block, StringComparison.Ordinal);
        Assert.DoesNotContain("brokerAddress", block, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalCommand", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_terminal_is_named_in_the_help_as_a_separate_tool()
    {
        // Somebody reading `parley --help` on a broker-only machine must be able to find out that
        // `terminal` needs a second package, without first running it and failing.
        var help = RepositoryFile.Read("src/Parley.Cli/Program.cs");

        Assert.Contains("parley-terminal", help, StringComparison.Ordinal);
    }

    [Fact]
    public void The_old_verb_is_gone_rather_than_kept_beside_the_new_one()
    {
        // No alias is kept for `top`, the old spelling of `terminal`: two spellings
        // of one command is a second thing for every document to mention.
        var program = RepositoryFile.Read("src/Parley.Cli/Program.cs");

        Assert.DoesNotContain("verb == \"top\"", program, StringComparison.Ordinal);
        Assert.DoesNotContain("\"parley-top\"", program, StringComparison.Ordinal);
    }
}
