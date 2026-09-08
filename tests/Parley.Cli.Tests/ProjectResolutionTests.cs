using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Which store, endpoint and catalogue a command ends up using, and why.
/// </summary>
/// <remarks>
/// <para>
/// Precedence is where the defects in this feature will be, so it is a pure function and every
/// combination is checked here rather than through a filesystem. The two cases that matter most
/// are the refusals: choosing a project when several are configured, and falling back when a
/// named one is absent, are both silent ways to act on the wrong deployment.
/// </para>
/// <para>
/// The parity cases matter nearly as much. With no manifest, resolution must be exactly what it
/// was before this existed — value for value — or an upgrade changes behaviour for somebody who
/// asked for nothing.
/// </para>
/// </remarks>
public sealed class ProjectResolutionTests
{
    private static readonly Func<string, string?> NoVariables = _ => null;

    private static ProjectSettings Project(
        string name = "orders",
        string store = "/srv/orders/parley.db",
        string endpoint = "http://127.0.0.1:51707",
        string? node = "/srv/orders/node.json") =>
        new(ProjectName.Create(name), store, endpoint, node, null);

    // ---- selection -------------------------------------------------------------------------

    [Fact]
    public void With_nothing_configured_and_none_named_there_is_nothing_to_choose()
    {
        Assert.True(ProjectResolution.TrySelect(null, [], out var project, out var problem));
        Assert.Null(project);
        Assert.Null(problem);
    }

    [Fact]
    public void With_one_configured_and_none_named_that_one_is_used()
    {
        Assert.True(ProjectResolution.TrySelect(null, [Project()], out var project, out _));
        Assert.Equal("orders", project!.Name.Value);
    }

    [Fact]
    public void With_several_configured_and_none_named_it_refuses_and_lists_them()
    {
        // Never chooses. Picking one silently is acting on a deployment nobody named.
        var projects = new[] { Project("orders"), Project("billing", "/b.db", "http://x:2", null) };

        Assert.False(ProjectResolution.TrySelect(null, projects, out var project, out var problem));
        Assert.Null(project);
        Assert.Contains("billing, orders", problem, StringComparison.Ordinal);
        Assert.Contains("--project", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_project_that_is_not_configured_is_refused_and_never_falls_back()
    {
        // Falling back here connects to a different deployment — plausibly a working one,
        // silently wrong, invisible from both ends of a message.
        Assert.False(ProjectResolution.TrySelect("stock", [Project()], out var project, out var problem));
        Assert.Null(project);
        Assert.Contains("stock", problem, StringComparison.Ordinal);
        Assert.Contains("orders", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Naming_a_project_on_a_machine_with_none_says_how_to_add_one()
    {
        Assert.False(ProjectResolution.TrySelect("orders", [], out _, out var problem));
        Assert.Contains("parley project add", problem, StringComparison.Ordinal);
    }

    // ---- parity with the behaviour that came before -----------------------------------------

    [Fact]
    public void With_no_project_every_value_resolves_as_it_did_before()
    {
        Assert.Null(ProjectResolution.Store(null, NoVariables, null).Value);
        Assert.Equal(BrokerEndpoint.Default, ProjectResolution.Endpoint(null, NoVariables, null).Value);
        Assert.Equal(NodeCatalogue.DefaultFileName, ProjectResolution.NodeConfig(null, NoVariables, null).Value);
    }

    [Fact]
    public void With_no_project_the_environment_still_wins_over_the_default()
    {
        Func<string, string?> variables = name => name switch
        {
            "PARLEY_DB" => "/from/env.db",
            "PARLEY_BROKER" => "http://env:1",
            "PARLEY_NODE_CONFIG" => "/from/env-node.json",
            _ => null,
        };

        Assert.Equal("/from/env.db", ProjectResolution.Store(null, variables, null).Value);
        Assert.Equal("http://env:1", ProjectResolution.Endpoint(null, variables, null).Value);
        Assert.Equal("/from/env-node.json", ProjectResolution.NodeConfig(null, variables, null).Value);
    }

    // ---- precedence -------------------------------------------------------------------------

    [Fact]
    public void An_argument_beats_everything()
    {
        Func<string, string?> variables = _ => "http://env:1";

        var resolved = ProjectResolution.Endpoint("http://given:2", variables, Project());

        Assert.Equal("http://given:2", resolved.Value);
        Assert.Equal(ValueSource.Argument, resolved.Source);
    }

    [Fact]
    public void A_variable_beats_a_project()
    {
        Func<string, string?> variables = name => name == "PARLEY_BROKER" ? "http://env:1" : null;

        var resolved = ProjectResolution.Endpoint(null, variables, Project());

        Assert.Equal("http://env:1", resolved.Value);
        Assert.Equal(ValueSource.Environment, resolved.Source);
    }

    [Fact]
    public void A_project_beats_the_default()
    {
        var resolved = ProjectResolution.NodeConfig(null, NoVariables, Project());

        Assert.Equal("/srv/orders/node.json", resolved.Value);
        Assert.Equal(ValueSource.Project, resolved.Source);
        Assert.Equal("orders", resolved.Detail);
    }

    [Fact]
    public void Precedence_applies_per_value_independently()
    {
        // A project supplies the store while an explicit endpoint overrides only the endpoint.
        // Resolving them together would make one flag silently discard two unrelated values.
        var project = Project();

        Assert.Equal(ValueSource.Project, ProjectResolution.Store(null, NoVariables, project).Source);
        Assert.Equal(ValueSource.Argument, ProjectResolution.Endpoint("http://given:2", NoVariables, project).Source);
        Assert.Equal(ValueSource.Project, ProjectResolution.NodeConfig(null, NoVariables, project).Source);
    }

    [Fact]
    public void A_project_without_a_catalogue_falls_back_rather_than_failing()
    {
        // A project may legitimately start nothing on this machine. That is not the same as a
        // misconfigured one, and must not be treated as an error.
        var resolved = ProjectResolution.NodeConfig(null, NoVariables, Project(node: null));

        Assert.Equal(NodeCatalogue.DefaultFileName, resolved.Value);
        Assert.Equal(ValueSource.Default, resolved.Source);
    }

    // ---- reporting ---------------------------------------------------------------------------

    [Fact]
    public void A_resolved_value_says_where_it_came_from()
    {
        // This line is the point of the feature: it is what was missing when enrolling would have
        // written consent into a catalogue nothing reads.
        Assert.Contains(
            "(project \"orders\")",
            ProjectResolution.NodeConfig(null, NoVariables, Project()).ToString(),
            StringComparison.Ordinal);

        Assert.Contains(
            "(given)",
            ProjectResolution.Endpoint("http://given:2", NoVariables, null).ToString(),
            StringComparison.Ordinal);

        Assert.Contains(
            "(PARLEY_BROKER)",
            ProjectResolution.Endpoint(null, _ => "http://env:1", null).ToString(),
            StringComparison.Ordinal);
    }
}
