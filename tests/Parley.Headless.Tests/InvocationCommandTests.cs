using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// Turning a launch-configuration string into something safe to execute.
/// </summary>
public sealed class InvocationCommandTests
{
    [Fact]
    public void The_protocol_flags_are_appended_rather_than_required()
    {
        // An operator writes the runner, not the wire format. A template that had to spell out
        // four protocol flags would get one of them wrong in one repository out of nineteen, and
        // the failure would look like that agent being broken rather than that line being wrong.
        var command = InvocationCommand.Parse("claude");

        Assert.Equal("claude", command.FileName);
        Assert.Equal(InvocationCommand.ProtocolArguments, command.Arguments);
    }

    [Fact]
    public void The_session_is_named_after_the_agent()
    {
        // A resident session is one somebody will open by hand later. Nineteen of them listed by
        // their first line rather than by name is a list nobody can use.
        var command = InvocationCommand.Parse("claude", "program-library");

        Assert.Equal(
            ["-n", "program-library", .. InvocationCommand.ProtocolArguments],
            command.Arguments);
    }

    [Fact]
    public void The_name_comes_from_the_registry_rather_than_the_template()
    {
        // It exists in the launch configuration already, as PARLEY_AGENT. A second copy beside
        // it is a copy that can disagree, and then the session listing names one agent while the
        // protocol names another -- with the listing being the one a person reads.
        var command = InvocationCommand.Parse("claude", "billing");

        Assert.Contains("billing", command.Arguments);
        Assert.DoesNotContain("PARLEY_AGENT", command.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_operator_who_named_the_session_is_not_overruled()
    {
        // Two -n flags leave the runner to pick between them, which is a coin toss written as
        // configuration.
        var command = InvocationCommand.Parse("claude -n mine", "billing");

        Assert.Equal(["-n", "mine", .. InvocationCommand.ProtocolArguments], command.Arguments);
        Assert.DoesNotContain("billing", command.Arguments);
    }

    [Fact]
    public void A_different_runner_is_not_given_this_runners_naming_flag()
    {
        // -n is Claude Code's spelling, not a universal one.
        var command = InvocationCommand.Parse("my-agent --input-format jsonl", "billing");

        Assert.DoesNotContain("-n", command.Arguments);
        Assert.DoesNotContain("billing", command.Arguments);
    }

    [Fact]
    public void An_operators_own_flags_are_kept_and_come_first()
    {
        var command = InvocationCommand.Parse("claude --permission-mode acceptEdits");

        Assert.Equal(
            ["--permission-mode", "acceptEdits", .. InvocationCommand.ProtocolArguments],
            command.Arguments);
    }

    [Fact]
    public void A_template_that_names_its_own_input_format_is_left_alone()
    {
        // The escape hatch for a runner that is not Claude Code. Appending flags meant for one
        // runner to a command line meant for another produces something neither understands.
        var command = InvocationCommand.Parse("my-agent --input-format jsonl --serve");

        Assert.Equal(["--input-format", "jsonl", "--serve"], command.Arguments);
        Assert.DoesNotContain("stream-json", command.Arguments);
    }

    [Fact]
    public void The_prompt_is_not_an_argument()
    {
        // It used to be, when a process was started per message and the prompt was the only way
        // to say anything to it. A resident agent is given its turn over standard input, so a
        // prompt on the command line would be a first turn nobody asked for.
        var command = InvocationCommand.Parse("claude");

        Assert.DoesNotContain(InvocationCommand.Prompt, command.Arguments);
    }

    [Fact]
    public void A_quoted_path_survives_being_split()
    {
        // Launch configurations name executables in Program Files.
        var command = InvocationCommand.Parse("\"C:/Program Files/x/claude.exe\" -q");

        Assert.Equal("C:/Program Files/x/claude.exe", command.FileName);
        Assert.Equal("-q", command.Arguments[0]);
    }

    [Fact]
    public void The_template_is_not_a_shell()
    {
        // Split on whitespace and nothing else: no pipes, no redirection, no expansion. This
        // string is executed by the supervisor, and a template a shell interpreted would turn a
        // typo in one repository's launch configuration into arbitrary execution on the machine
        // running the fleet.
        var command = InvocationCommand.Parse("claude && rm -rf /");

        Assert.Equal("claude", command.FileName);
        Assert.Contains("&&", command.Arguments);
        Assert.DoesNotContain(command.Arguments, a => a.Contains("&& rm", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_template_is_refused()
    {
        Assert.Throws<ArgumentException>(() => InvocationCommand.Parse("   "));
    }
}
