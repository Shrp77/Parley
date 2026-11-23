using System.Globalization;
using Grpc.Net.Client;
using Parley.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That freezing an agent reaches every project that serves it and only those, that freezing a whole project leaves out who
/// cannot be frozen and who already is, and that the person is told which projects an act reaches (SPEC.md §14).
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

    private static readonly AgentName Inv = AgentName.Create("inv@test");

    private static readonly AgentName Stock = AgentName.Create("stock@test");

    // Three repositories' identifiers (SPEC.md §4.1.1).
    private const string First = "6f0c3a52-8f4e-4f7b-9d7a-0a1b2c3d4e51";

    private const string Second = "6f0c3a52-8f4e-4f7b-9d7a-0a1b2c3d4e52";

    private const string Third = "6f0c3a52-8f4e-4f7b-9d7a-0a1b2c3d4e53";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_is_served_only_by_the_projects_that_list_it_and_have_not_retired_it()
    {
        using var deployment = new Deployment("alpha", "beta", "gamma", "delta");

        deployment["alpha"].Registry.Add(Row(Orders));
        deployment["beta"].Registry.Add(Row(Orders, Wire.AgentStatus.Retired));
        deployment["gamma"].Registry.Add(Row(Billing));
        deployment["delta"].FailsOn = "DescribeRegistry";

        var (serving, unreachable) = await AgentFreezing.ServedByAsync(deployment.Directory, "alpha", Orders, Ct);

        Assert.Equal([new ServedIn("alpha", Orders)], serving);

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

        var outcome = await AgentFreezing.FreezeAsync(deployment.Directory, "alpha", Orders, keep, Ct);

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

        var outcome = await AgentFreezing.UnfreezeAsync(deployment.Directory, "alpha", Orders, Ct);

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

        var outcome = await AgentFreezing.FreezeAsync(deployment.Directory, "alpha", Orders, cancellationToken: Ct);

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

        var outcomes = (await AgentFreezing.AllAsync(deployment.Directory, "alpha", freeze: true, cancellationToken: Ct)).Agents;

        Assert.Equal([Orders], outcomes.Select(o => o.Agent));
        Assert.Equal([Orders.Value], deployment["alpha"].Frozen.Select(f => f.Agent));
    }

    [Fact]
    public async Task Freezing_a_project_never_asks_for_the_persons_seat()
    {
        using var deployment = new Deployment("alpha");

        deployment["alpha"].Registry.Add(Row(Orders));
        deployment["alpha"].Registry.Add(Row(AgentName.Create($"{PersonSeat.Role}@alpha"), adapter: PersonSeat.Adapter));

        var outcomes = (await AgentFreezing.AllAsync(deployment.Directory, "alpha", freeze: true, cancellationToken: Ct)).Agents;

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

        var outcomes = (await AgentFreezing.AllAsync(deployment.Directory, "alpha", freeze, cancellationToken: Ct)).Agents;

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

        var outcomes = (await AgentFreezing.AllAsync(deployment.Directory, "alpha", freeze: true, keep, Ct)).Agents;

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

        var outcomes = (await AgentFreezing.AllAsync(deployment.Directory, "alpha", freeze: false, cancellationToken: Ct)).Agents;

        Assert.Equal([Billing], outcomes.Select(o => o.Agent));
        Assert.Equal([Billing.Value], deployment["alpha"].Unfrozen.Select(f => f.Agent));
        Assert.Empty(deployment["alpha"].Frozen);
    }

    [Fact]
    public async Task A_project_nobody_named_is_asked_nothing()
    {
        using var deployment = new Deployment("alpha");

        deployment["alpha"].Registry.Add(Row(Orders));

        var none = await AgentFreezing.AllAsync(deployment.Directory, "elsewhere", freeze: true, cancellationToken: Ct);

        Assert.Empty(none.Agents);
        Assert.Contains("no project named 'elsewhere'", none.Problem, StringComparison.Ordinal);
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

        Assert.Equal("This reaches alpha.", AgentFreezing.Reach(Orders, [new ServedIn("alpha", Orders)], []));
        Assert.Equal("This reaches alpha, beta.", AgentFreezing.Reach(Orders, [new ServedIn("alpha", Orders), new ServedIn("beta", Orders)], []));

        Assert.Equal(
            "This reaches alpha, beta. It cannot be reached in delta, epsilon, so it is not done there.",
            AgentFreezing.Reach(Orders, [new ServedIn("alpha", Orders), new ServedIn("beta", Orders)], [("delta", "it did not answer"), ("epsilon", "refused")]));
    }

    // Identity, not the name: one repository may carry a different name in each project, and two repositories may share a name
    // in two projects (SPEC.md §4.1.1). Matched by name, a freeze in one project froze an unrelated agent in another, missed the
    // same agent under another name, and let an agent that took another's name shed that agent's freeze (review, lens 2 run A on
    // 5de89187).

    [Fact]
    public async Task Two_repositories_that_share_a_name_in_two_projects_are_frozen_and_unfrozen_apart()
    {
        using var deployment = new Deployment("alpha", "beta");

        deployment["alpha"].Registry.Add(Row(Orders, identity: First));
        deployment["beta"].Registry.Add(Row(Orders, identity: Second));

        var frozen = await AgentFreezing.FreezeAsync(deployment.Directory, "beta", Orders, cancellationToken: Ct);

        Assert.Equal([Orders.Value], deployment["beta"].Frozen.Select(f => f.Agent));
        Assert.Empty(deployment["alpha"].Frozen);
        Assert.Equal(["beta"], frozen.Projects.Select(p => p.Project));

        var unfrozen = await AgentFreezing.UnfreezeAsync(deployment.Directory, "beta", Orders, Ct);

        Assert.Equal([Orders.Value], deployment["beta"].Unfrozen.Select(f => f.Agent));
        Assert.Empty(deployment["alpha"].Unfrozen);
        Assert.Equal(["beta"], unfrozen.Projects.Select(p => p.Project));
    }

    [Fact]
    public async Task One_repository_under_two_names_is_frozen_in_each_project_under_that_projects_name()
    {
        using var deployment = new Deployment("alpha", "beta");

        deployment["alpha"].Registry.Add(Row(Inv, identity: First));
        deployment["beta"].Registry.Add(Row(Stock, identity: First));

        var (serving, _) = await AgentFreezing.ServedByAsync(deployment.Directory, "alpha", Inv, Ct);

        Assert.Equal([new ServedIn("alpha", Inv), new ServedIn("beta", Stock)], serving);
        Assert.Equal("This reaches alpha, beta (as 'stock@test').", AgentFreezing.Reach(Inv, serving, []));

        var frozen = await AgentFreezing.FreezeAsync(deployment.Directory, "alpha", Inv, cancellationToken: Ct);

        Assert.Equal([Inv.Value], deployment["alpha"].Frozen.Select(f => f.Agent));
        Assert.Equal([Stock.Value], deployment["beta"].Frozen.Select(f => f.Agent));
        Assert.Contains("beta (as 'stock@test'): changed", AgentFreezing.Describe(frozen), StringComparison.Ordinal);
        Assert.DoesNotContain("alpha (as", AgentFreezing.Describe(frozen), StringComparison.Ordinal);

        await AgentFreezing.UnfreezeAsync(deployment.Directory, "beta", Stock, Ct);

        Assert.Equal([Inv.Value], deployment["alpha"].Unfrozen.Select(f => f.Agent));
        Assert.Equal([Stock.Value], deployment["beta"].Unfrozen.Select(f => f.Agent));
    }

    [Fact]
    public async Task The_same_identifier_on_another_machine_is_another_agent()
    {
        using var deployment = new Deployment("alpha", "beta");

        deployment["alpha"].Registry.Add(Row(Inv, identity: First));
        deployment["beta"].Registry.Add(Row(AgentName.Create("inv@elsewhere"), identity: First));

        await AgentFreezing.FreezeAsync(deployment.Directory, "alpha", Inv, cancellationToken: Ct);

        Assert.Single(deployment["alpha"].Frozen);
        Assert.Empty(deployment["beta"].Frozen);
    }

    [Fact]
    public async Task Freeze_all_reaches_each_agent_by_its_identity_and_names_it_as_the_other_project_does()
    {
        using var deployment = new Deployment("alpha", "beta");

        deployment["alpha"].Registry.Add(Row(Inv, identity: First));
        deployment["alpha"].Registry.Add(Row(Orders, identity: Second));
        deployment["beta"].Registry.Add(Row(Stock, identity: First));
        deployment["beta"].Registry.Add(Row(Orders, identity: Third));

        var all = await AgentFreezing.AllAsync(deployment.Directory, "alpha", freeze: true, cancellationToken: Ct);

        Assert.Equal([Inv.Value, Orders.Value], deployment["alpha"].Frozen.Select(f => f.Agent).Order(StringComparer.Ordinal));
        Assert.Equal([Stock.Value], deployment["beta"].Frozen.Select(f => f.Agent));
        Assert.Equal(" 'inv@test' was frozen in 'beta' (as 'stock@test') too.", AgentFreezing.Elsewhere(all, "alpha", freeze: true));
    }

    [Fact]
    public async Task Unfreeze_all_in_one_project_leaves_a_freeze_on_another_projects_agent_of_the_same_name()
    {
        using var deployment = new Deployment("alpha", "beta");

        // Beta's orders took alpha's agent's name; alpha's orders is frozen and stays so whatever beta's person does.
        deployment["alpha"].Registry.Add(Row(Orders, frozen: true, identity: Second));
        deployment["beta"].Registry.Add(Row(Orders, frozen: true, identity: Third));
        deployment["beta"].Registry.Add(Row(Stock, frozen: true, identity: First));
        deployment["alpha"].Registry.Add(Row(Inv, frozen: true, identity: First));

        var all = await AgentFreezing.AllAsync(deployment.Directory, "beta", freeze: false, cancellationToken: Ct);

        Assert.Equal([Orders.Value, Stock.Value], deployment["beta"].Unfrozen.Select(f => f.Agent).Order(StringComparer.Ordinal));
        Assert.Equal([Inv.Value], deployment["alpha"].Unfrozen.Select(f => f.Agent));
        Assert.Equal(" 'stock@test' was unfrozen in 'alpha' (as 'inv@test') too.", AgentFreezing.Elsewhere(all, "beta", freeze: false));
    }

    [Fact]
    public async Task An_agent_with_no_identity_is_matched_by_its_name()
    {
        using var deployment = new Deployment("alpha", "beta");

        // An adapter that presented no identifier registered by name alone, so the name is all there is.
        deployment["alpha"].Registry.Add(Row(Orders));
        deployment["beta"].Registry.Add(Row(Orders, identity: Second));

        await AgentFreezing.FreezeAsync(deployment.Directory, "alpha", Orders, cancellationToken: Ct);

        Assert.Single(deployment["alpha"].Frozen);
        Assert.Single(deployment["beta"].Frozen);
    }

    [Fact]
    public async Task A_project_whose_own_registry_cannot_be_read_asks_nobody_else()
    {
        using var deployment = new Deployment("alpha", "beta");

        deployment["alpha"].FailsOn = "DescribeRegistry";
        deployment["beta"].Registry.Add(Row(Orders, identity: Second));

        var outcome = await AgentFreezing.FreezeAsync(deployment.Directory, "alpha", Orders, cancellationToken: Ct);

        Assert.Empty(deployment["beta"].Frozen);
        Assert.Equal("alpha", Assert.Single(outcome.Projects).Project);
        Assert.False(outcome.Complete);
    }

    // Review, lens 3 run A on 81b6a1a7: the acting project's row is read whatever its standing there, which no test held.
    // Looked up among active rows only, an agent retired here but still serving another project was reached nowhere.
    [Fact]
    public async Task An_agent_retired_where_the_person_acts_is_still_frozen_where_it_serves()
    {
        using var deployment = new Deployment("alpha", "beta");

        deployment["alpha"].Registry.Add(Row(Inv, Wire.AgentStatus.Retired, identity: First));
        deployment["beta"].Registry.Add(Row(Stock, identity: First));

        var outcome = await AgentFreezing.FreezeAsync(deployment.Directory, "alpha", Inv, cancellationToken: Ct);

        Assert.Empty(deployment["alpha"].Frozen);
        Assert.Equal([Stock.Value], deployment["beta"].Frozen.Select(f => f.Agent));
        Assert.Equal(["beta"], outcome.Projects.Select(p => p.Project));
    }

    // Review, lens 3 run A on 81b6a1a7: a project where the act failed is named with the agent's name there too, as one where
    // it was done is, so the person knows which agent to look at in it.
    [Fact]
    public async Task A_project_where_it_could_not_be_done_says_the_agents_name_there()
    {
        using var deployment = new Deployment("alpha", "beta");

        deployment["alpha"].Registry.Add(Row(Inv, identity: First));
        deployment["beta"].Registry.Add(Row(Stock, identity: First));
        deployment["beta"].FailsOn = "/Freeze";

        var outcome = await AgentFreezing.FreezeAsync(deployment.Directory, "alpha", Inv, cancellationToken: Ct);

        Assert.Contains("beta (as 'stock@test'): not done.", AgentFreezing.Describe(outcome), StringComparison.Ordinal);
    }

    private static Wire.RegistryRow Row(
        AgentName agent,
        Wire.AgentStatus status = Wire.AgentStatus.Active,
        bool frozen = false,
        string adapter = "claude-code",
        string identity = "") => new()
        {
            Name = agent.Value,
            Adapter = adapter,
            Tier = Wire.AgentTier.Full,
            Status = status,
            FrozenUtc = frozen
            ? new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture)
            : string.Empty,
            Identity = identity,
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
