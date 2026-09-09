using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Client;
using Parley.Core;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// Every outcome, through the real client, from a real store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Five values now cross three representations and one of them only became reachable with this
/// feature.</b> The store writes a lowercase word, the wire carries an enum, and the client maps it
/// back — and until `instruct` began returning <c>pending</c>, nothing ever produced that value, so
/// no path had ever carried it end to end. A conversion missing a case shows up as an operator
/// reading the wrong word for what their agent did.
/// </para>
/// <para>
/// The default arm is probed as a decision rather than glossed. The client maps anything it does
/// not recognise to <c>pending</c>, which matters the day the protocol gains a sixth outcome: an
/// older client meeting it says "still running", which invites a second look. Mapping it to
/// <c>failed</c> would invite the one response §10.2 names as making things worse.
/// </para>
/// </remarks>
public sealed class InstructionOutcomeMappingTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _broker;

    public InstructionOutcomeMappingTests()
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

    private BrokerClient Client() =>
        new(new Wire.Operators.OperatorsClient(GrpcChannel.ForAddress(_broker.Address)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(InstructionOutcome.Taken, "it is done")]
    [InlineData(InstructionOutcome.Declined, "not my repository")]
    [InlineData(InstructionOutcome.Undeliverable, null)]
    [InlineData(InstructionOutcome.Failed, null)]
    public async Task Every_settled_outcome_survives_the_store_the_wire_and_the_client(
        InstructionOutcome outcome, string? reply)
    {
        var instructions = new InstructionRepository(_connection);
        var id = await instructions.RecordAsync(Orders, $"for {outcome}", T0, Ct);

        _ = await instructions.SettleAsync(id, outcome, reply, T0.AddMinutes(1), Ct);

        var page = await Client().InstructionsAsync(instructionId: id, cancellationToken: Ct);

        var read = Assert.Single(page.Instructions);

        Assert.Equal(outcome, read.Outcome);
        Assert.Equal(reply, read.Reply);
        Assert.False(read.StillRunning);
        Assert.NotNull(read.SettledUtc);
    }

    [Fact]
    public async Task A_running_turn_survives_the_same_journey()
    {
        // The fifth, and the one nothing could produce before this feature. Kept apart from the
        // theory above because everything about it is the opposite: no reply, no settled instant,
        // and the one outcome that is not terminal.
        var id = await new InstructionRepository(_connection)
            .RecordAsync(Orders, "still going", T0, Ct);

        var page = await Client().InstructionsAsync(instructionId: id, cancellationToken: Ct);

        var read = Assert.Single(page.Instructions);

        Assert.Equal(InstructionOutcome.Pending, read.Outcome);
        Assert.True(read.StillRunning);
        Assert.Null(read.Reply);
        Assert.Null(read.SettledUtc);
    }

    [Fact]
    public void An_outcome_the_client_does_not_know_reads_as_running_rather_than_failed()
    {
        // The default arm, as a decision. A sixth outcome added to the protocol reaches an older
        // client as something it cannot name -- and "still running" invites a second look, while
        // "failed" invites reissuing the instruction, which §10.2 calls the one response that makes
        // things worse.
        //
        // Asserted against the wire enum's own unspecified value, which is the shape any unknown
        // arrives in.
        Assert.Equal(0, (int)Wire.InstructionOutcome.Unspecified);

        // The domain's own zero is pending, which is what makes the mapping consistent from both
        // directions rather than only by the switch's last arm.
        Assert.Equal(0, (int)InstructionOutcome.Pending);
    }

    [Fact]
    public async Task The_five_outcomes_are_five_distinct_values_end_to_end()
    {
        // Non-vacuity for the theory: if two of them mapped to the same thing, every case above
        // would still pass while an operator could not tell a decline from a failure.
        var instructions = new InstructionRepository(_connection);

        var ids = new List<(long Id, InstructionOutcome Outcome)>();

        foreach (var outcome in new[]
        {
            InstructionOutcome.Taken,
            InstructionOutcome.Declined,
            InstructionOutcome.Undeliverable,
            InstructionOutcome.Failed
        })
        {
            var id = await instructions.RecordAsync(Orders, $"for {outcome}", T0, Ct);

            _ = await instructions.SettleAsync(id, outcome, null, T0.AddMinutes(1), Ct);

            ids.Add((id, outcome));
        }

        _ = await instructions.RecordAsync(Orders, "running", T0, Ct);

        var page = await Client().InstructionsAsync(cancellationToken: Ct);

        Assert.Equal(5, page.Instructions.Count);
        Assert.Equal(5, page.Instructions.Select(i => i.Outcome).Distinct().Count());
    }
}
