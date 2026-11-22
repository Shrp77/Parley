using System.Globalization;
using Grpc.Net.Client;
using Parley.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That freezing an agent reaches every project that serves it and only those, that freezing a whole project leaves out who
/// cannot be frozen and who already is, and that the person is told which projects an act reaches (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Several projects, each with a broker of its own</b>, answered from a script that records what it was asked: the claim
/// is about which broker was asked what, and one process serves every project an agent is in. A freeze that reached one
/// project only would end the process and let the next project's work start it again.
/// </para>
/// <para>
/// Here rather than beside the client's other tests because this project has the scripted broker. No page is served: the
/// directory is built over the scripts and <see cref="AgentFreezing"/> is called as both surfaces call it.
/// </para>
/// </remarks>
[Integration]
public sealed class AgentFreezingAcrossProjectsTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_is_served_only_by_the_projects_that_list_it_and_have_not_retired_it()
    {
        using var deployment = new Deployment("alpha", "beta", "gamma", "delta");

        deployment["alpha"].Registry.Add(Row(Orders));
        deployment["beta"].Registry.Add(Row(Orders, Wire.AgentStatus.Retired));
        deployment["gamma"].Registry.Add(Row(Billing));
        deployment["delta"].FailsOn = "DescribeRegistry";

        var (serving, unreachable) = await AgentFreezing.ServedByAsync(deployment.Directory, Orders, Ct);

        Assert.Equal(["alpha"], serving);

        // The one that could not be asked is named with why, never taken for a project that does not list it.
        var down = Assert.Single(unreachable);

        Assert.Equal("delta", down.Project);
        Assert.False(string.IsNullOrWhiteSpace(down.Problem));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Freezing_asks_every_project_that_serves_the_agent_and_no_other_and_carries_the_choice_to_keep_it(bool keep)
    {
        using var deployment = new Deployment("alpha", "beta", "gamma");

        deployment["alpha"].Registry.Add(Row(Orders));
        deployment["beta"].Registry.Add(Row(Orders));
        deployment["gamma"].Registry.Add(Row(Billing));

        var outcome = await AgentFreezing.FreezeAsync(deployment.Directory, Orders, keep, Ct);

        foreach (var project in new[] { "alpha", "beta" })
        {
            var asked = Assert.Single(deployment[project].Frozen);

            Assert.Equal(Orders.Value, asked.Agent);
            Assert.Equal(keep, asked.KeepFrozen);
            Assert.Empty(deployment[project].Unfrozen);
        }

        Assert.Empty(deployment["gamma"].Frozen);
        Assert.Empty(deployment["gamma"].Unfrozen);
        Assert.Equal(["alpha", "beta"], outcome.Projects.Select(p => p.Project));
        Assert.True(outcome.Complete);
        Assert.True(outcome.AnyChanged);
    }

    [Fact]
    public async Task Unfreezing_asks_every_project_that_serves_the_agent_to_unfreeze_it_and_freezes_nothing()
    {
        using var deployment = new Deployment("alpha", "beta", "gamma");

        deployment["alpha"].Registry.Add(Row(Orders, frozen: true));
        deployment["beta"].Registry.Add(Row(Orders, frozen: true));
        deployment["gamma"].Registry.Add(Row(Billing, frozen: true));

        var outcome = await AgentFreezing.UnfreezeAsync(deployment.Directory, Orders, Ct);

        foreach (var project in new[] { "alpha", "beta" })
        {
            Assert.Equal(Orders.Value, Assert.Single(deployment[project].Unfrozen).Agent);
            Assert.Empty(deployment[project].Frozen);
        }

        Assert.Empty(deployment["gamma"].Unfrozen);
        Assert.True(outcome.Complete);

        // An unfreeze says nothing of a process: there is none to say it of.
        Assert.All(outcome.Projects, p => Assert.Null(p.Process));
    }

    [Fact]
    public async Task A_project_that_cannot_be_reached_is_reported_and_the_rest_are_still_frozen()
    {
        using var deployment = new Deployment("alpha", "beta");

        deployment["alpha"].Registry.Add(Row(Orders));
        deployment["beta"].FailsOn = "DescribeRegistry";

        var outcome = await AgentFreezing.FreezeAsync(deployment.Directory, Orders, cancellationToken: Ct);

        Assert.Single(deployment["alpha"].Frozen);
        Assert.Empty(deployment["beta"].Frozen);
        Assert.False(outcome.Complete);

        var beta = outcome.Projects.Single(p => p.Project == "beta");

        Assert.False(beta.Changed);
        Assert.NotNull(beta.Problem);
        Assert.Contains("beta: not done.", AgentFreezing.Describe(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Freezing_a_project_never_asks_for_the_facilitator()
    {
        using var deployment = new Deployment("alpha");

        deployment["alpha"].Registry.Add(Row(Orders));
        deployment["alpha"].Registry.Add(Row(AgentName.Create($"{Facilitation.Role}@alpha"), adapter: Facilitation.Adapter));

        var outcomes = await AgentFreezing.AllAsync(deployment.Directory, "alpha", freeze: true, cancellationToken: Ct);

        Assert.Equal([Orders], outcomes.Select(o => o.Agent));
        Assert.Equal([Orders.Value], deployment["alpha"].Frozen.Select(f => f.Agent));
    }

    [Fact]
    public async Task Freezing_a_project_never_asks_for_the_persons_seat()
    {
        using var deployment = new Deployment("alpha");

        deployment["alpha"].Registry.Add(Row(Orders));
        deployment["alpha"].Registry.Add(Row(AgentName.Create($"{PersonSeat.Role}@alpha"), adapter: PersonSeat.Adapter));

        var outcomes = await AgentFreezing.AllAsync(deployment.Directory, "alpha", freeze: true, cancellationToken: Ct);

        Assert.Equal([Orders], outcomes.Select(o => o.Agent));
        Assert.Equal([Orders.Value], deployment["alpha"].Frozen.Select(f => f.Agent));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Acting_on_a_project_leaves_out_a_retired_agent(bool freeze)
    {
        using var deployment = new Deployment("alpha");

        // Both in the state the act would change, so only being retired keeps the second out.
        deployment["alpha"].Registry.Add(Row(Orders, frozen: !freeze));
        deployment["alpha"].Registry.Add(Row(Billing, Wire.AgentStatus.Retired, frozen: !freeze));

        var outcomes = await AgentFreezing.AllAsync(deployment.Directory, "alpha", freeze, cancellationToken: Ct);

        Assert.Equal([Orders], outcomes.Select(o => o.Agent));
        Assert.DoesNotContain(deployment["alpha"].Frozen, f => f.Agent == Billing.Value);
        Assert.DoesNotContain(deployment["alpha"].Unfrozen, f => f.Agent == Billing.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Freezing_a_project_skips_who_is_already_frozen_and_reaches_the_rest_wherever_they_are_served(bool keep)
    {
        using var deployment = new Deployment("alpha", "beta");

        deployment["alpha"].Registry.Add(Row(Orders));
        deployment["alpha"].Registry.Add(Row(Billing, frozen: true));
        deployment["beta"].Registry.Add(Row(Orders));
        deployment["beta"].Registry.Add(Row(Billing));

        var outcomes = await AgentFreezing.AllAsync(deployment.Directory, "alpha", freeze: true, keep, Ct);

        Assert.Equal([Orders], outcomes.Select(o => o.Agent));

        // The project's agents, in every project that serves each: beta is asked for the one alpha lists as not frozen, and
        // not for the one alpha already holds frozen, though beta does not.
        foreach (var project in new[] { "alpha", "beta" })
        {
            var asked = Assert.Single(deployment[project].Frozen);

            Assert.Equal(Orders.Value, asked.Agent);
            Assert.Equal(keep, asked.KeepFrozen);
            Assert.Empty(deployment[project].Unfrozen);
        }
    }

    [Fact]
    public async Task Unfreezing_a_project_asks_only_for_who_is_frozen_and_freezes_nobody()
    {
        using var deployment = new Deployment("alpha");

        deployment["alpha"].Registry.Add(Row(Orders));
        deployment["alpha"].Registry.Add(Row(Billing, frozen: true));

        var outcomes = await AgentFreezing.AllAsync(deployment.Directory, "alpha", freeze: false, cancellationToken: Ct);

        Assert.Equal([Billing], outcomes.Select(o => o.Agent));
        Assert.Equal([Billing.Value], deployment["alpha"].Unfrozen.Select(f => f.Agent));
        Assert.Empty(deployment["alpha"].Frozen);
    }

    [Fact]
    public async Task A_project_nobody_named_is_asked_nothing()
    {
        using var deployment = new Deployment("alpha");

        deployment["alpha"].Registry.Add(Row(Orders));

        Assert.Empty(await AgentFreezing.AllAsync(deployment.Directory, "elsewhere", freeze: true, cancellationToken: Ct));
        Assert.Equal(0, deployment["alpha"].Calls);
    }

    [Fact]
    public void The_reach_names_the_projects_or_says_there_are_none_and_names_those_that_could_not_be_asked()
    {
        Assert.Equal(
            "No project that can be reached lists 'orders@test'.",
            AgentFreezing.Reach(Orders, [], []));

        // None listing it is said even when some could not be asked: nothing would be done, so nothing is promised.
        Assert.Equal(
            "No project that can be reached lists 'orders@test'.",
            AgentFreezing.Reach(Orders, [], [("delta", "it did not answer")]));

        Assert.Equal("This reaches alpha.", AgentFreezing.Reach(Orders, ["alpha"], []));
        Assert.Equal("This reaches alpha, beta.", AgentFreezing.Reach(Orders, ["alpha", "beta"], []));

        Assert.Equal(
            "This reaches alpha, beta. It cannot be reached in delta, epsilon, so it is not done there.",
            AgentFreezing.Reach(Orders, ["alpha", "beta"], [("delta", "it did not answer"), ("epsilon", "refused")]));
    }

    private static Wire.RegistryRow Row(
        AgentName agent,
        Wire.AgentStatus status = Wire.AgentStatus.Active,
        bool frozen = false,
        string adapter = "claude-code") => new()
        {
            Name = agent.Value,
            Adapter = adapter,
            Tier = Wire.AgentTier.Full,
            Status = status,
            FrozenUtc = frozen
            ? new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture)
            : string.Empty,
        };

    /// <summary>Several projects, each answered by a script of its own.</summary>
    private sealed class Deployment : IDisposable
    {
        private readonly Dictionary<string, RoadmapBroker> _brokers = new(StringComparer.Ordinal);

        public Deployment(params string[] projects)
        {
            var settings = new List<ProjectSettings>();

            foreach (var (name, at) in projects.Select((name, at) => (name, at)))
            {
                var address = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{5500 + at}");

                _brokers[address] = new RoadmapBroker();
                settings.Add(new ProjectSettings(ProjectName.Create(name), $"{name}.db", address, null));
            }

            // Nothing is dialled: every address is answered by its script, so the port is only a name.
            Directory = new BrokerDirectory(
                settings,
                address => GrpcChannel.ForAddress(
                    address, new GrpcChannelOptions { HttpHandler = _brokers[address], DisposeHttpClient = false }));
        }

        public BrokerDirectory Directory { get; }

        public RoadmapBroker this[string project] => _brokers[Directory.Find(project)!.Endpoint];

        public void Dispose()
        {
            Directory.Dispose();

            foreach (var broker in _brokers.Values)
            {
                broker.Dispose();
            }
        }
    }
}
