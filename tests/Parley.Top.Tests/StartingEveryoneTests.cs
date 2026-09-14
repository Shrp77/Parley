using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That an agent nothing has registered can be made addressable without opening its repository,
/// and that this screen never says one is when it has not looked.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's report, and it is the one that matters most.</b> Nineteen repositories were
/// enrolled, each was reported as started, and every one of them came back from a broadcast as
/// <c>nothing has registered under that name yet</c>. §14 counts such a name unroutable and refuses
/// every message type to it, so nothing they could send would ever have woken one — and the two
/// remedies left were opening nineteen repositories or starting nineteen rows one at a time, which
/// is the work 012 step 1 exists to remove.
/// </para>
/// <para>
/// <b>Two defects, and the first is why the second went unnoticed.</b> The add screen predicted the
/// registration instead of checking it — "It registers itself as it attaches" — so a start that
/// launched nothing, or launched something that never attached, read exactly like one that worked.
/// </para>
/// </remarks>
public sealed class StartingEveryoneTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public StartingEveryoneTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    private ProjectSettings Project() =>
        new(Walking.Alpha, "unused.db", _broker.Address, null, null);

    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));

    private static AgentRecord Agent(AgentName name) =>
        new(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active);

    /// <summary>A catalogue naming roles this machine may start, written as the node reads one.</summary>
    private static NodeCatalogue Catalogue(params string[] roles)
    {
        var path = Path.Combine(Path.GetTempPath(), "parley-start-" + Path.GetRandomFileName() + ".json");

        var entries = roles.Select(role =>
            $"    \"{role}\": {{ \"command\": \"claude\", \"workspace\": \"/srv/{role}\" }}");

        File.WriteAllText(path, "{\n  \"agents\": {\n" + string.Join(",\n", entries) + "\n  }\n}\n");

        try
        {
            return NodeCatalogue.ReadFrom(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_agents_pane_offers_to_start_everything_nothing_has_registered()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await Walking.GoAsync(shell, Viewing.Project);

        // One registered, two catalogued and never run — the shape of a fresh enrolment.
        shell.Agents.Show([Agent(Orders)], Catalogue("orders", "billing", "inventory"), null);

        var waiting = shell.NeverRegistered();

        Assert.Equal(2, waiting.Count);
        Assert.DoesNotContain(Orders, waiting);

        Assert.Contains(
            shell.Actions(),
            action => action.Label == Shell.StartEveryTitle(2));
    }

    [Fact]
    public async Task Nothing_is_offered_when_every_agent_has_registered()
    {
        // The other side of it: an action offered on a deployment with nothing to start would be a
        // key that does nothing, on the screen somebody looks at most.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent(Orders), Agent(Billing)], Catalogue("orders", "billing"), null);

        Assert.Empty(shell.NeverRegistered());
        Assert.DoesNotContain(shell.Actions(), action => action.Label.StartsWith("Start the", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_start_that_registered_nothing_is_reported_as_that_rather_than_as_success()
    {
        // **The defect that made the other one invisible.** The node answers that it launched
        // something; the registry is what says whether an agent arrived. This broker has no node
        // at all, so nothing starts and nothing registers — and the report has to say so.
        var reported = await Starting.OnceAsync(
            Orders,
            (_, _) => Task.FromResult(ProjectRead<bool>.Read(true)),
            (_, _) => Task.FromResult(ProjectRead<bool>.Read(false)),
            TimeSpan.Zero,
            TimeProvider.System,
            TestContext.Current.CancellationToken);

        Assert.False(reported.Registered);
        Assert.Equal(Starting.NotSeen, reported.Says);
    }

    [Fact]
    public async Task A_start_the_node_refused_says_the_node_refused_it()
    {
        var reported = await Starting.OnceAsync(
            Orders,
            (_, _) => Task.FromResult(ProjectRead<bool>.Read(false)),
            (_, _) => Task.FromResult(ProjectRead<bool>.Read(true)),
            TimeSpan.Zero,
            TimeProvider.System,
            TestContext.Current.CancellationToken);

        Assert.False(reported.Registered);
        Assert.Equal(Starting.NotStarted, reported.Says);

        // And it did not go on to claim a registration it never looked for.
        Assert.DoesNotContain(Starting.Registered, reported.Says, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registration_that_arrives_is_reported_as_registered()
    {
        var looks = 0;

        var reported = await Starting.OnceAsync(
            Orders,
            (_, _) => Task.FromResult(ProjectRead<bool>.Read(true)),
            (_, _) =>
            {
                looks++;

                return Task.FromResult(ProjectRead<bool>.Read(true));
            },
            TimeSpan.Zero,
            TimeProvider.System,
            TestContext.Current.CancellationToken);

        Assert.True(reported.Registered);
        Assert.Equal(Starting.Registered, reported.Says);
        Assert.Equal(1, looks);
    }

    [Fact]
    public async Task The_set_is_reported_by_what_happened_rather_than_by_a_list_of_names()
    {
        // The broadcast report's shape, for the same reason: a terminal shows one row, so counts
        // first, cause next, names last and abbreviated.
        var agents = new[]
        {
            AgentName.Create("one@test"), AgentName.Create("two@test"),
            AgentName.Create("three@test"), AgentName.Create("four@test"),
            AgentName.Create("five@test"), AgentName.Create("six@test"),
            AgentName.Create("seven@test"),
        };

        var said = await Starting.EveryAsync(
            agents,
            (agent, _) => Task.FromResult(ProjectRead<bool>.Read(agent.Role != "six")),
            (agent, _) => Task.FromResult(ProjectRead<bool>.Read(agent.Role == "one")),
            TimeSpan.Zero,
            TimeProvider.System,
            saying: null,
            TestContext.Current.CancellationToken);

        Assert.StartsWith("Registered 1 of 7.", said, StringComparison.Ordinal);
        Assert.Contains("5: " + Starting.NotSeen, said, StringComparison.Ordinal);
        Assert.Contains("1: " + Starting.NotStarted, said, StringComparison.Ordinal);

        // Abbreviated rather than nineteen names running off the end of the line.
        Assert.Contains("and 1 more", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_registry_is_what_says_whether_an_agent_registered()
    {
        // **Everything above rests on this one read**, and it is asked of a real broker over a
        // socket rather than of a double: §12.3 makes registration the adapter's own act on
        // attach, so the registry is the only evidence there is, and a check that quietly
        // answered from the roster would be this surface believing its own drawing.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        Assert.False(
            (await shell.RegisteredIn(Project(), Orders, TestContext.Current.CancellationToken))
                .Value,
            "an agent nothing has registered was reported as registered");

        await new AgentRepository(_connection).UpsertAsync(
            Agent(Orders), TestContext.Current.CancellationToken);

        var seen = await shell.RegisteredIn(
            Project(), Orders, TestContext.Current.CancellationToken);

        Assert.True(seen.Reachable);
        Assert.True(seen.Value, "a registered agent was not found in the registry");
    }

    [Fact]
    public async Task Every_start_is_asked_for_before_anything_is_waited_on()
    {
        // **The property that keeps nineteen agents from costing nine minutes.** A wait each is
        // thirty seconds times the roster, spent by a surface whose work queue is serialised; one
        // wait for the set is one window however many there are. Checked by ordering rather than by
        // timing: every start must have been asked for before the first look at the registry.
        var order = new List<string>();

        var agents = Enumerable.Range(0, 5)
            .Select(at => AgentName.Create($"agent-{at}@test"))
            .ToList();

        var said = await Starting.EveryAsync(
            agents,
            (agent, _) =>
            {
                order.Add("start " + agent.Role);

                return Task.FromResult(ProjectRead<bool>.Read(true));
            },
            (agent, _) =>
            {
                order.Add("look " + agent.Role);

                return Task.FromResult(ProjectRead<bool>.Read(true));
            },
            TimeSpan.Zero,
            TimeProvider.System,
            saying: null,
            TestContext.Current.CancellationToken);

        Assert.StartsWith("Registered 5 of 5.", said, StringComparison.Ordinal);
        Assert.Equal(5, order.Count(step => step.StartsWith("start ", StringComparison.Ordinal)));

        var firstLook = order.FindIndex(step => step.StartsWith("look ", StringComparison.Ordinal));

        Assert.Equal(5, firstLook);
    }

    [Fact]
    public async Task It_says_what_it_is_doing_while_it_does_it()
    {
        // Nineteen launches and a wait is a minute of a screen that would otherwise say nothing,
        // and a screen that says nothing is one somebody assumes has hung.
        var heard = new List<string>();

        _ = await Starting.EveryAsync(
            [Orders, Billing],
            (_, _) => Task.FromResult(ProjectRead<bool>.Read(true)),
            (_, _) => Task.FromResult(ProjectRead<bool>.Read(true)),
            TimeSpan.Zero,
            TimeProvider.System,
            heard.Add,
            TestContext.Current.CancellationToken);

        Assert.Contains(heard, said => said.Contains("Starting 1 of 2", StringComparison.Ordinal));
        Assert.Contains(heard, said => said.Contains("Starting 2 of 2", StringComparison.Ordinal));
        Assert.Contains(heard, said => said.Contains("Waiting for them", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Starting_them_asks_this_projects_broker_for_each_one()
    {
        // Driven against a real broker over a socket, so the operation being asked for is the one
        // §10.2 defines rather than a double's idea of it. Nothing is catalogued here and no node
        // is attached, so every start is refused — which is the honest answer and the one the
        // owner's deployment would give if its node were gone.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([], Catalogue("orders", "billing"), null);

        await shell.StartEvery();

        Assert.StartsWith("Registered 0 of 2.", shell.Said, StringComparison.Ordinal);
        Assert.Contains(Starting.NotStarted, shell.Said, StringComparison.Ordinal);
    }
}
