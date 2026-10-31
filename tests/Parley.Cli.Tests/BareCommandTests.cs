using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What <c>parley</c> with no arguments does.
/// </summary>
/// <remarks>
/// <para>
/// The other half of the operator operations living in the terminal: if that is where a deployment is read and changed, typing the tool's name should
/// put somebody in it. <c>htop</c> and <c>top</c> behave the same way.
/// </para>
/// <para>
/// <b>Every test here runs the tool with its streams redirected, which is the case that must not
/// open a terminal</b> — and is also the only case a test can have. A suite cannot give a child
/// process a real console, so what is proved here is the fallback; that the other branch launches
/// `parley-terminal` is one line, and the guard around it is what these hold.
/// </para>
/// </remarks>
[Integration]
public sealed class BareCommandTests
{
    [Fact]
    public void With_no_arguments_and_a_redirected_stream_it_prints_the_commands()
    {
        // **A pipe, a script or CI.** `parley | grep serve`, or something checking the tool exists,
        // must get text and an exit code rather than a full-screen application it cannot draw and
        // nobody can quit. A hung pipeline is the worst version of this: it looks like the machine
        // is busy.
        var (code, output, error) = ToolProcess.RunFully([]);

        Assert.Equal(64, code);
        Assert.Contains("parley serve", output + error, StringComparison.Ordinal);
        // The way on is the terminal now, not a command in this list: `project add` was removed on
        // 2026-09-25 and the help stopped offering it with the tool.
        Assert.Contains("`parley`",output + error, StringComparison.Ordinal);
    }

    [Fact]
    public void It_does_not_need_a_broker_to_answer()
    {
        // Somebody typing the name on a machine where nothing is running is asking what this is.
        // Answering "no broker is listening" tells them nothing they can act on, least of all that
        // there is a list of commands to see.
        var (_, output, error) = ToolProcess.RunFully(
            [("PARLEY_BROKER", "http://127.0.0.1:1")]);

        Assert.DoesNotContain("No broker is listening", output + error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_commands_it_lists_are_the_ones_that_still_exist()
    {
        // The listing is what somebody reads instead of the terminal when they cannot open one, so
        // a verb that moved must not be in it. `PluginTests` holds the same property from the other
        // side by comparing this file's verbs against the operator skill's; this asserts it against
        // what the tool actually prints, which is the thing a person sees.
        var (_, output, error) = ToolProcess.RunFully([]);
        var said = output + error;

        foreach (var moved in new[] { "parley export", "parley purge", "parley prune", "parley flags" })
        {
            Assert.DoesNotContain(moved, said, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void It_says_where_a_deployment_is_read_and_changed()
    {
        // The listing has to name the terminal, because it is now the answer to most of what
        // somebody arriving here wants to do. A command list with sixteen operations quietly absent
        // and nothing saying where they went is how a person concludes the tool lost them.
        var (_, output, error) = ToolProcess.RunFully([]);

        Assert.Contains("`parley`",output + error, StringComparison.Ordinal);
    }
}
