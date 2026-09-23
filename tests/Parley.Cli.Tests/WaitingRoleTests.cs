using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That `parley waiting` takes a bare role as that role on this machine, and a command acting on an agent does not.
/// </summary>
/// <remarks>
/// A launch configuration names a role and the adapter appends the machine, so the waiting hook reading one has a role
/// to pass and nothing else. `waiting` refused it as not an agent name, which kept the hook silent in every enrolled
/// repository on the machine where the sweep over project-scoped callers found it. Nothing listens at the address, so a
/// name that parsed reaches the transport and says so (69), and one that did not is refused first (64).
/// </remarks>
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

    [Fact]
    public void A_command_that_acts_on_an_agent_still_takes_only_the_whole_name()
    {
        var (code, error) = ToolProcess.Run([(BrokerEndpoint.Variable, Nowhere)], "retire", "orders");

        Assert.Equal(64, code);
        Assert.Contains("is not an agent name", error, StringComparison.Ordinal);
    }
}
