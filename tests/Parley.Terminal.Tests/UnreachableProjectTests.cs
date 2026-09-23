using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Core;
using Parley.Store;
using Xunit;
using static Parley.Cli.Tests.Loopback;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a project whose broker is not answering says so, and leaves every other project usable.
/// </summary>
/// <remarks>
/// <para>
/// SC-008 and User Story 1's fourth scenario. The containment is the point: a deployment is
/// several brokers and one of them being down is ordinary, so a surface where the dead one takes
/// the screen with it would be useless exactly when somebody opened it to find out what was wrong.
/// </para>
/// <para>
/// The dead endpoint is a port nothing listens on rather than a broker that was stopped, because a
/// stopped broker's port may still be held for a moment and the state being tested is "nothing
/// there at all".
/// </para>
/// <para>
/// <b>It came from <c>FreePort</c> and could not.</b> That returns a port the kernel has just
/// offered, which something else may then take — and it did, in a full-suite run on Linux, where
/// this failed while passing six times out of six in isolation. The ephemeral range is shared
/// across every test host, and giving the terminal a host of its own added an eleventh process
/// drawing from it. <see cref="Loopback.NothingListens"/> is outside that range.
/// </para>
/// </remarks>
public sealed class UnreachableProjectTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;
    private readonly string _dead = NothingListens();

    public UnreachableProjectTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task A_project_whose_broker_is_not_answering_says_so_and_the_others_still_work()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;
        driving.App.LayoutAndDraw(true);

        // The dead one is named as dead, in words rather than by a colour.
        Assert.Contains(ProjectsRegion.NotAnswering, driving.Text(), StringComparison.Ordinal);

        // And the live one is still readable, from the same screen, without restarting anything.
        driving.Row(shell.Projects.List, "alive");
        await shell.Settled;

        Assert.Equal("alive", shell.Projects.Selected?.Name.Value);
        Assert.Contains(shell.Agents.Rows, row => row.Name == Orders.Value);
    }

    [Fact]
    public async Task Selecting_the_unreachable_project_leaves_its_regions_saying_why()
    {
        // Rather than empty. An agents region that showed nothing would read as a project with no
        // agents, which is a different fact and a much less alarming one.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Row(shell.Projects.List, "dead");
        await shell.Settled;
        driving.App.LayoutAndDraw(true);

        Assert.Empty(shell.Agents.Rows);
        Assert.Empty(shell.Conversations.Conversations);
        Assert.DoesNotContain(AgentsRegion.Nothing, driving.Text(), StringComparison.Ordinal);
    }

    private BrokerDirectory Directory() =>
        new(
            [
                new ProjectSettings(ProjectName.Create("dead"), string.Empty, _dead, null, null),
                new ProjectSettings(ProjectName.Create("alive"), string.Empty, _broker.Address, null, null),
            ],
            endpoint => GrpcChannel.ForAddress(endpoint));
}
