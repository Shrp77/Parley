using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That the terminal actually offers, and runs, the instructions readback.
/// </summary>
/// <remarks>
/// <b>Written because <c>DeadSurfaceTests</c> demanded it.</b> The action and its title were
/// reachable only from their own file, which that guard reports as dead code — and it was right in
/// the way that matters: <c>TellingTests</c> checked what the dialog renders and nothing checked
/// that anything opened it. The repository has been bitten by exactly this before, with a status
/// line computed correctly and drawn nowhere.
/// </remarks>
public sealed class TellingActionTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Arvind = AgentName.Create("arvind@test");
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _broker;

    public TellingActionTests()
    {
        _broker = LoopbackOperator.Start(_connection);

        new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, _broker.Address, null, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    private async Task<long> TellAsync(string text, string? reply)
    {
        var instructions = new InstructionRepository(_connection);
        var token = TestContext.Current.CancellationToken;
        var id = await instructions.RecordAsync(Orders, text, T0, token);

        if (reply is not null)
        {
            _ = await instructions.SettleAsync(
                id, InstructionOutcome.Taken, reply, T0.AddMinutes(1), token);
        }

        return id;
    }

    [Fact]
    public async Task The_action_is_offered_on_an_agent()
    {
        // contracts/surface.md: no action exists only as a key. The letter is how somebody finds
        // out the readback is there at all.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        driving.Row(shell.Agents.List, Orders.Value);
        await shell.Settled;

        Assert.Contains(
            shell.Actions(),
            action => action.Label.StartsWith(Shell.ToldTitle, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Running_it_opens_what_the_operator_said_and_what_came_back()
    {
        _ = await TellAsync("look at the retry policy", "I changed three files");

        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        driving.Row(shell.Agents.List, Orders.Value);
        await shell.Settled;

        await shell.ShowTold();
        await shell.Settled;

        Assert.NotNull(shell.Told);

        var shown = shell.Told!.Shown;

        Assert.Contains(shown, line => line.Contains("retry policy", StringComparison.Ordinal));
        Assert.Contains(shown, line => line.Contains("three files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_seat_is_not_offered_it()
    {
        // A seat is a person, and nobody instructs themselves. Offering it would put an action on a
        // row where it can only ever be empty.
        await new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(
                Arvind, Client.Seating.Seat.Adapter, AgentTier.Pull, null, null, null, false,
                AgentStatus.Active),
            TestContext.Current.CancellationToken);

        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        driving.Row(shell.Agents.List, Arvind.Value);
        await shell.Settled;

        Assert.True(shell.Agents.Selected?.Seat, "the cursor is not on the seat");

        Assert.DoesNotContain(
            shell.Actions(),
            action => action.Label.StartsWith(Shell.ToldTitle, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_agent_told_nothing_says_so_rather_than_opening_an_empty_box()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        driving.Row(shell.Agents.List, Orders.Value);
        await shell.Settled;

        await shell.ShowTold();
        await shell.Settled;

        Assert.NotNull(shell.Told);
        Assert.Equal(Telling.Nothing, Assert.Single(shell.Told!.Shown));
    }
}
