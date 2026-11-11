using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the note saying where an agent's next message went is placed after the message it followed, read through the
/// operator's client over the wire (SPEC.md §5.0).
/// </summary>
/// <remarks>
/// The server's half is held in <c>Parley.Server.Tests</c> and the drawing in <c>Parley.Client.Tests</c>, each with
/// numbers it made itself. Neither holds the client's mapping of the wire's two message numbers, and a mapping that read
/// the wrong one would move the note to after the message written elsewhere, below words the agent did not wait for.
/// </remarks>
[Integration]
public sealed class WroteElsewhereThroughTheClientTests : IAsyncDisposable
{
    private const string Here = "in-flight";
    private const string Older = "sync-around";

    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Person = AgentName.Create("human-user@test");
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Parley.Broker.Broker _broker;
    private readonly LoopbackOperator _operator;

    public WroteElsewhereThroughTheClientTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00')
                ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
              ('orders@test', 'test', 'full', 'active'),
              ('billing@test', 'test', 'full', 'active');
            INSERT INTO agents (name, adapter, tier, status) VALUES ('human-user@test', 'parley-console', 'full', 'active')
                ON CONFLICT DO NOTHING;
            """);

        _broker = new Parley.Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
        _operator = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _operator.DisposeAsync();
        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> AskAsync(AgentName from, AgentName to, string ask, string conversation)
    {
        await _broker.StartAsync(from, T0, Ct);

        return (await _broker.RequestAsync(from, to, "model-a", $$"""{"ask":"{{ask}}"}""", T0, conversation, Ct)).Exchange!.Id;
    }

    [Fact]
    public async Task The_note_sits_after_the_agents_last_message_here_and_before_what_others_said_next()
    {
        // orders answers here, the person then asks billing here, and only after that does orders write to billing in
        // another conversation. The note belongs between orders' answer and the person's question: what orders did
        // next, after the last thing it said here.
        var asked = await AskAsync(Person, Orders, "Sort the pricing script", Here);

        _ = await _broker.ResultAsync(
            asked, Orders, "model-a", $$"""{"outcomes":[{"id":"{{PayloadShape.WholeRequest}}","outcome":"done","text":"sorted it"}]}""",
            T0.AddMinutes(1), cancellationToken: Ct);

        var answered = _connection.ExecuteScalar<long>("SELECT MAX(id) FROM messages;");

        _ = await AskAsync(Person, Billing, "Check the invoice totals", Here);
        _ = await AskAsync(Orders, Billing, "Is the script yours?", Older);

        var export = await new BrokerClient(_operator.Operators).ExportAsync(Here, Ct);

        var noted = Assert.Single(export.WroteElsewhere!);

        Assert.Equal(answered, noted.AfterMessage);
        Assert.DoesNotContain(export.Messages, message => message.Id == noted.Message);

        var lines = Reading.Lines(export, TimeZoneInfo.Utc, Here).Select(line => line.Text).ToList();
        var note = lines.FindIndex(line => line.Contains("orders@test's next message went to billing@test in sync-around", StringComparison.Ordinal));

        Assert.InRange(
            note,
            lines.FindIndex(line => line.Contains("sorted it", StringComparison.Ordinal)) + 1,
            lines.FindIndex(line => line.Contains("Check the invoice totals", StringComparison.Ordinal)) - 1);
    }
}
