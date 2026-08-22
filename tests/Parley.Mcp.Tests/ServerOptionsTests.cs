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
public sealed class ServerOptionsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] set)
    {
        var map = set.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static (string, string)[] Minimal =>
        [(ServerOptions.DatabaseVariable, "p.db"), (ServerOptions.AgentVariable, "orders")];

    [Fact]
    public void The_two_required_variables_are_enough()
    {
        var options = ServerOptions.Read(Env(Minimal));

        Assert.Equal("p.db", options.DatabasePath);
        Assert.Equal("orders", options.Agent.Value);
        Assert.Equal(ServerOptions.DefaultTickInterval, options.TickInterval);
        Assert.Null(options.Workspace);
    }

    [Theory]
    [InlineData("PARLEY_DB")]
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
            Env((ServerOptions.DatabaseVariable, "p.db"), (ServerOptions.AgentVariable, "   "))));

        Assert.Contains(ServerOptions.AgentVariable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unusable_agent_name_is_refused_with_the_rule()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ServerOptions.Read(
            Env((ServerOptions.DatabaseVariable, "p.db"), (ServerOptions.AgentVariable, "Orders"))));

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
    public void The_tick_interval_is_read_in_seconds()
    {
        var options = ServerOptions.Read(Env([.. Minimal, (ServerOptions.TickVariable, "15")]));

        Assert.Equal(TimeSpan.FromSeconds(15), options.TickInterval);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-30")]
    [InlineData("a minute")]
    [InlineData("1.5")]
    public void An_unusable_tick_interval_is_refused_rather_than_defaulted(string configured)
    {
        // Refused rather than ignored, because an operator who set it meant something by it and
        // a server quietly sweeping at a cadence they did not choose gives them no way to find
        // out. Zero and a negative are refused for the further reason that PeriodicTimer throws
        // on both, which would surface as a crash at start-up with no mention of the variable.
        var ex = Assert.Throws<InvalidOperationException>(() => ServerOptions.Read(
            Env([.. Minimal, (ServerOptions.TickVariable, configured)])));

        Assert.Contains(ServerOptions.TickVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains(configured, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_tick_interval_falls_back_rather_than_failing()
    {
        var options = ServerOptions.Read(Env([.. Minimal, (ServerOptions.TickVariable, "  ")]));

        Assert.Equal(ServerOptions.DefaultTickInterval, options.TickInterval);
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
    public void The_database_variable_is_named_in_one_place()
    {
        // It had two spellings: a constant here and a bare literal in the operator's command, in
        // assemblies that deliberately do not reference each other. They agreed by coincidence,
        // and nothing would have noticed them diverging — the operator would have administered a
        // database the agents were not using, and both would have reported success.
        //
        // This fails if somebody replaces the reference with a literal again, which is the only
        // way they can now come apart.
        Assert.Equal(Parley.Core.DeploymentEnvironment.DatabaseVariable, ServerOptions.DatabaseVariable);
    }
}
