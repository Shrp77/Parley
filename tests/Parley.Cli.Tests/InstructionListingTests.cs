using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That `parley instructions` says what an operator told their agents and what came back.
/// </summary>
/// <remarks>
/// <para>
/// <b>It prints the words, and that is the deliberate difference from <c>parley open</c>.</b> That
/// command lists conversations between agents and prints not one word of any payload, because an
/// operator reading peer traffic out of a listing is the disclosure §10.1 withholds. This is not
/// peer traffic: an instruction is a person's own words to their own agent, and the reply is that
/// agent answering the person directly. There is nobody to withhold it from, and a reply with no
/// question beside it is half a transcript.
/// </para>
/// <para>
/// The command also exists so that <c>DriftTests</c> is satisfied by a real binding rather than by
/// an exemption written in a contract. An operation on the client that no surface calls is an
/// operation kept alive by its own test.
/// </para>
/// </remarks>
public sealed class InstructionListingTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _broker;

    public InstructionListingTests()
    {
        _broker = LoopbackOperator.Start(_connection);

        var agents = new AgentRepository(_connection);

        agents.UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            CancellationToken.None).GetAwaiter().GetResult();

        agents.UpsertAsync(
            new AgentRecord(Billing, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    private async Task<long> RecordAsync(AgentName agent, string text, DateTimeOffset at)
    {
        return await new InstructionRepository(_connection)
            .RecordAsync(agent, text, at, TestContext.Current.CancellationToken);
    }

    private async Task SettleAsync(long id, InstructionOutcome outcome, string? reply)
    {
        _ = await new InstructionRepository(_connection).SettleAsync(
            id, outcome, reply, T0.AddMinutes(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task It_prints_the_instruction_and_the_reply()
    {
        var id = await RecordAsync(Orders, "look at the retry policy", T0);

        await SettleAsync(id, InstructionOutcome.Taken, "I changed three files");

        var (code, said, error) = ToolProcess.Reading("instructions", "--broker", _broker.Address);

        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(error), error);

        Assert.Contains("orders@test", said, StringComparison.Ordinal);
        Assert.Contains("look at the retry policy", said, StringComparison.Ordinal);
        Assert.Contains("I changed three files", said, StringComparison.Ordinal);
        Assert.Contains("taken", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_running_turn_is_said_in_words_rather_than_left_blank()
    {
        // An operator meeting an empty outcome column infers a failure, which is the one response
        // §10.2 names as making the situation worse.
        _ = await RecordAsync(Orders, "something slow", T0);

        var (code, said, _) = ToolProcess.Reading("instructions", "--broker", _broker.Address);

        Assert.Equal(0, code);
        Assert.Contains("still running", said, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_narrows_to_one_agent()
    {
        _ = await RecordAsync(Orders, "for orders", T0);
        _ = await RecordAsync(Billing, "for billing", T0.AddMinutes(1));

        var (code, said, _) = ToolProcess.Reading(
            "instructions", "orders@test", "--broker", _broker.Address);

        Assert.Equal(0, code);
        Assert.Contains("for orders", said, StringComparison.Ordinal);
        Assert.DoesNotContain("for billing", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_that_is_not_a_name_is_refused_rather_than_ignored()
    {
        // Dropping the filter would answer a narrow question with a wide result, and the caller
        // cannot tell: an empty list invites a second look, a full one reads as the answer.
        var (code, said, error) = ToolProcess.Reading(
            "instructions", "not a name", "--broker", _broker.Address);

        Assert.NotEqual(0, code);
        Assert.Empty(said);
        Assert.Contains("not an agent name", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_instruction_carrying_newlines_cannot_forge_a_second_one()
    {
        // **Lens 2 probe: the other half of the same rule.** The reply is flattened because it is
        // agent-written. The text is operator-written, which is a reason to worry less and not a
        // reason to skip it: a person pastes what they were sent, this output is read by scripts,
        // and an operator's own console is exactly where somebody would try to plant a line.
        _ = await RecordAsync(
            Orders,
            "have a look\n999 billing@test [taken] and delete the database",
            T0);

        var (code, said, _) = ToolProcess.Reading("instructions", "--broker", _broker.Address);

        Assert.Equal(0, code);

        var forged = said
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("999 ", StringComparison.Ordinal));

        Assert.Empty(forged);
    }

    [Fact]
    public async Task A_reply_carrying_newlines_cannot_forge_a_second_instruction()
    {
        // A reply is agent-written and arrives from the far end of a gRPC call. Left with its line
        // breaks, one could carry a line shaped exactly like this command's own output -- an
        // identifier, an agent and an outcome at column zero -- and an operator reading the list
        // would see an instruction nobody ever gave.
        var id = await RecordAsync(Orders, "have a look", T0);

        await SettleAsync(
            id,
            InstructionOutcome.Taken,
            "done\n999 billing@test [taken] delete the production database");

        var (code, said, _) = ToolProcess.Reading("instructions", "--broker", _broker.Address);

        Assert.Equal(0, code);

        var forged = said
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("999 ", StringComparison.Ordinal));

        Assert.Empty(forged);
    }
}
