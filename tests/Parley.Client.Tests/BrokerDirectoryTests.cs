using Grpc.Core;
using Grpc.Net.Client;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// The console's multi-project handling: one project's failure is that project's failure.
/// </summary>
public sealed class BrokerDirectoryTests
{
    private static ProjectSettings Project(string name, string endpoint) =>
        new(ProjectName.Create(name), $"{name}.db", endpoint, null, null);

    private static GrpcChannel Fake(string endpoint) => GrpcChannel.ForAddress(endpoint);

    [Fact]
    public async Task An_unreachable_broker_does_not_fail_the_read()
    {
        var outcome = await BrokerDirectory.ReadAsync<int>(
            () => throw new RpcException(new Status(StatusCode.Unavailable, "no broker")));

        Assert.False(outcome.Reachable);
        Assert.NotNull(outcome.Problem);
    }

    [Fact]
    public async Task One_unreachable_project_leaves_another_readable()
    {
        var down = await BrokerDirectory.ReadAsync<string>(
            () => throw new RpcException(new Status(StatusCode.Unavailable, "no broker")));

        var up = await BrokerDirectory.ReadAsync(() => Task.FromResult("roster"));

        Assert.False(down.Reachable);
        Assert.True(up.Reachable);
        Assert.Equal("roster", up.Value);
    }

    [Fact]
    public async Task A_refusal_that_is_not_unavailable_is_still_contained()
    {
        var outcome = await BrokerDirectory.ReadAsync<int>(
            () => throw new RpcException(new Status(StatusCode.Unimplemented, "no such method")));

        Assert.False(outcome.Reachable);
        Assert.Contains("refused", outcome.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_projects_holding_the_same_agent_name_stay_distinct()
    {
        // SPEC.md §4.1: names collide freely across projects, because each is a separate store.
        // Nothing may key on a name alone; the key is always (project, agent).
        using var directory = new BrokerDirectory(
            [Project("alpha", "http://127.0.0.1:1"), Project("beta", "http://127.0.0.1:2")],
            Fake);

        var alpha = directory.Find("alpha");
        var beta = directory.Find("beta");

        Assert.NotNull(alpha);
        Assert.NotNull(beta);
        Assert.NotEqual(alpha!.Endpoint, beta!.Endpoint);
    }

    [Fact]
    public void An_unknown_project_name_finds_nothing_rather_than_guessing()
    {
        using var directory = new BrokerDirectory([Project("alpha", "http://127.0.0.1:1")], Fake);

        Assert.Null(directory.Find("gamma"));
        Assert.Null(directory.Find(null));
    }

    [Fact]
    public void A_machine_with_no_manifest_reports_no_projects_and_no_problem()
    {
        // An ordinary machine, not a broken one. The distinction matters because the page must
        // not render "no projects" and "manifest unreadable" the same way.
        using var directory = new BrokerDirectory([], Fake);

        Assert.Empty(directory.Projects);
        Assert.Null(directory.ManifestProblem);
    }

    [Fact]
    public void An_unreadable_manifest_is_reported_rather_than_shown_as_emptiness()
    {
        using var directory = new BrokerDirectory([], Fake, "projects.json is not a JSON object.");

        Assert.Empty(directory.Projects);
        Assert.NotNull(directory.ManifestProblem);
    }

    [Fact]
    public void One_channel_is_held_per_endpoint()
    {
        var opened = 0;

        using var directory = new BrokerDirectory(
            [Project("alpha", "http://127.0.0.1:1")],
            endpoint =>
            {
                opened++;
                return GrpcChannel.ForAddress(endpoint);
            });

        var project = directory.Projects[0];
        _ = directory.ClientFor(project);
        _ = directory.ClientFor(project);

        Assert.Equal(1, opened);
    }
}
