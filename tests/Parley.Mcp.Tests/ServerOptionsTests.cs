using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// Reading the launch configuration.
/// </summary>
/// <remarks>
/// Worth testing despite being a handful of string reads, because this is the only code in the
/// server that runs before anything can report a problem through the protocol. A mistake here
/// does not surface as a wrong answer; it surfaces as a server the client says nothing about.
/// </remarks>
[Unit]
public sealed class ServerOptionsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] set)
    {
        var map = set.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static (string, string)[] Minimal =>
        [(ServerOptions.AgentVariable, "orders@test")];

    [Fact]
    public void One_required_variable_is_enough()
    {
        // One. The database is not the adapter's business
        // and the broker has a default, so the only thing a launch configuration must say
        // is who this process speaks for -- which is the one thing that could never be defaulted,
        // because guessing it would guess an identity.
        var options = ServerOptions.Read(Env(Minimal));

        Assert.Equal("orders@test", options.Agent.Value);
        Assert.Equal(Parley.Core.BrokerEndpoint.Default, options.Broker);
        Assert.Null(options.Workspace);
    }

    [Fact]
    public void The_broker_can_be_named_and_needs_no_scheme()
    {
        // A bare host and port is what people type, and there is only one scheme available:
        // SPEC.md §16 makes the transport plaintext, so https would be a promise nothing keeps.
        var options = ServerOptions.Read(
            Env([.. Minimal, (ServerOptions.BrokerVariable, "broker.example:51707")]));

        Assert.Equal("http://broker.example:51707", options.Broker);
    }

    [Fact]
    public void A_broker_address_that_is_not_one_is_refused_rather_than_defaulted()
    {
        // Silently using the local broker would put this agent in a different deployment from
        // the one somebody configured -- where its peers are absent, its name may belong to
        // somebody else, and nothing looks broken.
        var ex = Assert.Throws<InvalidOperationException>(() => ServerOptions.Read(
            Env([.. Minimal, (ServerOptions.BrokerVariable, "not an address")])));

        Assert.Contains(ServerOptions.BrokerVariable, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PARLEY_AGENT")]
    public void A_missing_required_variable_names_itself(string missing)
    {
        // The message is the whole value of failing here rather than later, so it is asserted
        // rather than assumed: whoever wrote the launch configuration needs to know which line
        // of it is wrong.
        var present = Minimal.Where(e => e.Item1 != missing).ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() => ServerOptions.Read(Env(present)));

        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variable_set_to_whitespace_counts_as_missing()
    {
        // Which is not pedantry: a launch configuration with "PARLEY_AGENT": "" is a real way to
        // get here, and treating it as present would produce an agent name that fails validation
        // with a message about the character set rather than about the variable being empty.
        var ex = Assert.Throws<InvalidOperationException>(() => ServerOptions.Read(
            Env((ServerOptions.BrokerVariable, "p.db"), (ServerOptions.AgentVariable, "   "))));

        Assert.Contains(ServerOptions.AgentVariable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unusable_agent_name_is_refused_with_the_rule()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ServerOptions.Read(
            Env((ServerOptions.BrokerVariable, "p.db"), (ServerOptions.AgentVariable, "Orders"))));

        Assert.Contains("Orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("lowercase", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_responder_defaults_rather_than_being_required()
    {
        // §11.3 makes attribution a MUST on every message, so there has to be something to
        // record when the launch configuration says nothing.
        var options = ServerOptions.Read(Env(Minimal));

        Assert.False(string.IsNullOrWhiteSpace(options.ResponderModel));
    }

    [Fact]
    public void The_responder_is_taken_from_the_environment_when_set()
    {
        var options = ServerOptions.Read(Env(
            [.. Minimal, (ServerOptions.ResponderVariable, "some-model-9")]));

        Assert.Equal("some-model-9", options.ResponderModel);
    }

    [Theory]
    [InlineData("parley:broker")]
    [InlineData("PARLEY:Broker")]
    [InlineData("parley:anything")]
    public void A_responder_claiming_the_reserved_namespace_is_refused(string claimed)
    {
        // §11.3 reserves this namespace and makes refusing an adapter that claims it a MUST,
        // so the broker refuses every message such a server would send. Caught here, the
        // launch fails with the reason; missed here, the server starts perfectly and then
        // fails every operation — reporting a launch-configuration fault to the model, which
        // can do nothing about it.
        var ex = Assert.Throws<InvalidOperationException>(() => ServerOptions.Read(
            Env([.. Minimal, (ServerOptions.ResponderVariable, claimed)])));

        Assert.Contains(ServerOptions.ResponderVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains("11.3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sweep_interval_is_not_the_adapters_to_set()
    {
        // This process does not read the interval, so there is no test of its parsing here.
        //
        // The sweep enforces every deadline in SPEC.md §6, and a deployment that ran twenty of
        // them -- one per adapter, each on its own cadence -- would have twenty answers to "how
        // often are deadlines checked" and no way to see any of them. There is one, in the
        // broker, and the variable is read there.
        Assert.DoesNotContain(
            typeof(ServerOptions).GetProperties(),
            p => p.Name.Contains("Tick", StringComparison.Ordinal));
    }

    [Fact]
    public void The_workspace_is_optional_and_not_invented()
    {
        // Deliberately not defaulted to the current directory. An MCP client chooses the working
        // directory of the process it launches and several do not choose the project, so a
        // default would record something plausible and wrong in the field an operator uses to
        // tell two similarly named agents apart.
        var set = ServerOptions.Read(Env([.. Minimal, (ServerOptions.WorkspaceVariable, "/src/o")]));
        var unset = ServerOptions.Read(Env(Minimal));

        Assert.Equal("/src/o", set.Workspace);
        Assert.Null(unset.Workspace);
    }

    [Fact]
    public void The_broker_variable_is_named_in_one_place()
    {
        // The database variable had two spellings once: a constant in the server and a bare
        // literal in the operator's command, in assemblies that deliberately do not reference
        // each other. They agreed by coincidence, and nothing would have noticed them diverging
        // — the operator would have administered a database the agents were not using, and both
        // would have reported success.
        //
        // The same hazard moved to the broker's address, and it is worse there: three processes
        // read it now rather than two, and a server that connected to a different broker than
        // the one everything else uses would produce a deployment where an agent is registered,
        // healthy, and invisible to every peer.
        //
        // This fails if somebody replaces the reference with a literal again, which is the only
        // way they can come apart.
        Assert.Equal(Parley.Core.BrokerEndpoint.Variable, ServerOptions.BrokerVariable);
    }
}
