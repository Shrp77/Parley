using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That `parley waiting` takes a bare role as that role on this machine.
/// </summary>
/// <remarks>
/// <para>
/// A launch configuration names a role and the adapter appends the machine, so the waiting hook reading one has a role
/// to pass and nothing else. `waiting` refused it as not an agent name, which kept the hook silent in every enrolled
/// repository on the machine where the sweep over project-scoped callers found it. Nothing listens at the address, so a
/// name that parsed reaches the transport and says so (69), and one that did not is refused first (64).
/// </para>
/// <para>
/// This also carried the other half of the rule — that a command which <i>acts</i> on an agent takes the whole name and
/// nothing less — until every such verb moved to <c>parley terminal</c>. See the note at the foot of the class.
/// </para>
/// </remarks>
[Integration]
public sealed class WaitingRoleTests
{
    private const string Nowhere = "http://127.0.0.1:1";

    [Fact]
    public void Waiting_takes_a_bare_role_as_that_role_on_this_machine()
    {
        var (code, error) = ToolProcess.Run([(BrokerEndpoint.Variable, Nowhere)], "waiting", "orders");

        Assert.Equal(69, code);
        Assert.DoesNotContain("is not an agent name", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Waiting_still_takes_a_whole_name()
    {
        var (code, _) = ToolProcess.Run([(BrokerEndpoint.Variable, Nowhere)], "waiting", "orders@elsewhere");

        Assert.Equal(69, code);
    }

    // **`A_command_that_acts_on_an_agent_still_takes_only_the_whole_name` was here and its subject
    // has gone.** It ran `parley retire orders` and asserted 64 with "is not an agent name",
    // because completing a bare role with this machine is right for reading an inbox and wrong for
    // acting on an agent: `waiting orders` asks about the wrong inbox at worst, `retire orders`
    // withdraws the wrong agent.
    //
    // The contrast needs two sides and there is one left. Every verb that acts on an agent —
    // `retire`, `reinstate`, `purge`, `instruct`, `start` — moved to `parley terminal`, and
    // `WithAgent`, which carries that refusal, is now reachable only through `WithAgentHere`, which
    // is the completing form this file's first two tests cover. Writing the check against a verb
    // that no longer exists would assert the removal's own exit code, which
    // `MovedToTheTerminalTests` asserts once for all sixteen.
    //
    // Recorded rather than deleted silently: the rule is live on the surface that took the verbs —
    // the terminal acts on the agent under the cursor and takes its whole name typed back — and if
    // a command that acts on an agent ever returns to this tool, the completion must not follow it.
}
