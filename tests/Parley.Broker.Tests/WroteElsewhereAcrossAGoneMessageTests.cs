using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That a gone message an agent only received, or only sent, still stops a note saying it went elsewhere straight after
/// (SPEC.md §5.0).
/// </summary>
/// <remarks>
/// The read drops a note when a message of the agent's between the two is gone, "sent or received". The retention case in
/// <see cref="WroteElsewhereTests"/> removes an exchange holding both, so either half of the check alone passed it
/// (review, lens 3 surface A on d99806d9). Here a destroyed conversation holds one message, to the agent or from it.
/// </remarks>
[Integration]
public sealed class WroteElsewhereAcrossAGoneMessageTests : IDisposable
{
    private const string Here = "in-flight";
    private const string Gone = "side-talk";
    private const string Older = "sync-around";

    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;
    private readonly OperatorConsole _console;

    public WroteElsewhereAcrossAGoneMessageTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00')
                ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
              ('orders@test', 'test', 'full', 'active'),
              ('billing@test', 'test', 'full', 'active'),
              ('inventory@test', 'test', 'full', 'active');
            """);

        _broker = new Broker(_connection, BrokerOptions.Default);
        _console = new OperatorConsole(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> AskAsync(AgentName from, AgentName to, string ask, string conversation, int minute)
    {
        await _broker.StartAsync(from, T0, Ct);

        return (await _broker.RequestAsync(from, to, "model-a", $$"""{"ask":"{{ask}}"}""", T0.AddMinutes(minute), conversation, Ct))
            .Exchange!.Id;
    }

    private long LastMessage() => _connection.ExecuteScalar<long>("SELECT MAX(id) FROM messages;");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_destroyed_message_between_drops_the_note(bool ordersOnlyReceivedIt)
    {
        // Received only: an answer to orders on an exchange it opened, before the message here, in the conversation that
        // is then destroyed, so the one gone message between is one orders did not write. Orders then holds nothing, so
        // its next Request goes where it names.
        var asked = ordersOnlyReceivedIt ? await AskAsync(Orders, Inventory, "Stock levels?", Gone, 0) : 0;

        await AskAsync(Orders, Billing, "Which ledger?", Here, 1);

        if (ordersOnlyReceivedIt)
        {
            await _broker.StartAsync(Inventory, T0, Ct);
            _ = await _broker.ResultAsync(
                asked, Inventory, "model-a", $$"""{"outcomes":[{"id":"{{PayloadShape.WholeRequest}}","outcome":"done","text":"forty"}]}""", T0.AddMinutes(2), Ct);
        }
        else
        {
            await AskAsync(Orders, Billing, "Stock levels?", Gone, 2);
        }

        await AskAsync(Orders, Billing, "Monthly close?", Older, 3);

        var later = LastMessage();

        Assert.Equal(Older, _connection.ExecuteScalar<string>("SELECT conversation_id FROM messages WHERE id = @later;", new { later }));

        var destroyed = await _console.DestroyConversationAsync(Gone, T0.AddMinutes(4), Ct);

        Assert.Equal(DestroyVerdict.Gone, destroyed.Verdict);
        Assert.Equal(1, _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM messages WHERE id = @later;", new { later }));

        var notes = (await _console.ExportAsync(Here, Ct)).WroteElsewhere!;

        Assert.DoesNotContain(notes, note => note.Message == later);
    }

    [Fact]
    public async Task A_message_between_that_a_purge_removed_with_its_record_drops_the_note()
    {
        // A purge removes the agent's messages and their records of length, so nothing says whose the missing id was.
        var asked = await AskAsync(Orders, Inventory, "Stock levels?", Gone, 0);

        await AskAsync(Orders, Billing, "Which ledger?", Here, 1);

        await _broker.StartAsync(Inventory, T0, Ct);
        _ = await _broker.ResultAsync(
            asked, Inventory, "model-a", $$"""{"outcomes":[{"id":"{{PayloadShape.WholeRequest}}","outcome":"done","text":"forty"}]}""", T0.AddMinutes(2), Ct);

        var answer = LastMessage();

        await AskAsync(Orders, Billing, "Monthly close?", Older, 3);

        var later = LastMessage();

        _ = await _console.PurgeAsync(Inventory, Ct);

        Assert.Equal(0, _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM message_lengths WHERE message_id = @answer;", new { answer }));
        Assert.Equal(1, _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM messages WHERE id = @later;", new { later }));

        Assert.DoesNotContain((await _console.ExportAsync(Here, Ct)).WroteElsewhere!, note => note.Message == later);
    }
}
