using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That Enter on a seat takes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported by the owner: a seat was on the screen, Enter did nothing, and `j` with the name
/// typed did nothing either.</b> Two faults were stacked. The messages this surface writes reached
/// no screen at all, so the second attempt reported its refusal into the void; and the seat in
/// question was retired, which the broker refuses outright — so there was a real failure underneath
/// the silence.
/// </para>
/// <para>
/// The name is already on the screen under the cursor, so Enter is what somebody presses. A
/// retired seat asks first, because reinstating is an operator operation and this surface does not
/// take one silently — but it is the person's own name rather than another agent's, so what it
/// needs is a sentence and a deliberate answer, not a name typed back.
/// </para>
/// </remarks>
public sealed class SeatOnEnterTests : IAsyncDisposable
{
    private static readonly AgentName Arvind = AgentName.Create("arvind@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _broker;

    public SeatOnEnterTests() => _broker = LoopbackOperator.Start(_connection);

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));

    private ProjectSettings Project() =>
        new(ProjectName.Create("alpha"), string.Empty, _broker.Address, null, null);

    private async Task RegisterSeatAsync(AgentStatus status)
    {
        await new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(Arvind, Seat.Adapter, AgentTier.Pull, null, null, null, false, status),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Enter_on_a_seat_takes_it()
    {
        await RegisterSeatAsync(AgentStatus.Active);

        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        driving.Row(shell.Agents.List, Arvind.Value);
        await shell.Settled;

        Assert.True(shell.Agents.Selected?.Seat, "the cursor is not on the seat");

        await shell.SeatSelected();
        await shell.Settled;

        Assert.True(shell.Held is not null, "not seated. said: " + shell.Said);
        Assert.Equal(Arvind, shell.Held!.Seat.Name);

        // And it said so, on the screen rather than only in a property.
        Assert.Contains(AgentsRegion.Took, shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retired_seat_is_described_before_anything_is_reinstated()
    {
        await RegisterSeatAsync(AgentStatus.Retired);

        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        driving.Row(shell.Agents.List, Arvind.Value);
        await shell.Settled;

        await shell.SeatSelected();
        await shell.Settled;

        driving.App.LayoutAndDraw(true);

        var drawn = driving.Text();

        // The description, and both answers, with the one that changes nothing first.
        Assert.Contains(Shell.RetiredSeatTitle, drawn, StringComparison.Ordinal);
        Assert.Contains(Shell.LeaveRetired, drawn, StringComparison.Ordinal);
        Assert.Contains(Shell.ReinstateAndSit, drawn, StringComparison.Ordinal);

        // Nothing has happened yet: the dialog is a question, not a report.
        Assert.Null(shell.Held);
    }

    [Fact]
    public async Task Taking_a_seat_is_offered_and_not_only_a_key()
    {
        // contracts/surface.md: no action exists only as a key. Enter is the shortcut somebody
        // reaches for; the letter is how they find out it is there.
        await RegisterSeatAsync(AgentStatus.Active);

        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        driving.Row(shell.Agents.List, Arvind.Value);
        await shell.Settled;

        Assert.Contains(
            shell.Actions(),
            action => action.Label.StartsWith(Shell.TakeThisSeat, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_agent_that_is_not_a_seat_is_left_alone_by_enter()
    {
        // There is no one obvious thing Enter should do to a peer, and guessing would be a key
        // doing something the status line never said it would.
        await new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(
                AgentName.Create("orders@test"), "mcp", AgentTier.Pull, null, null, null, false,
                AgentStatus.Active),
            TestContext.Current.CancellationToken);

        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        driving.Row(shell.Agents.List, "orders@test");
        await shell.Settled;

        await shell.SeatSelected();
        await shell.Settled;

        Assert.Null(shell.Held);

        Assert.DoesNotContain(
            shell.Actions(),
            action => action.Label.StartsWith(Shell.TakeThisSeat, StringComparison.Ordinal));
    }
}
