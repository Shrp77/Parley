using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That the person can close one Request without answering it, and only their own (SPEC.md §10.2, <c>close_request</c>).
/// </summary>
[Integration]
public sealed class ClosingARequestTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Person = AgentName.Create("human-user@test");
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;
    private readonly OperatorConsole _console;

    public ClosingARequestTests()
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

        _broker = new Broker(_connection, BrokerOptions.Default);
        _console = new OperatorConsole(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> AskAsync(AgentName from, AgentName to, string ask, string conversation = "pricing")
    {
        await _broker.StartAsync(from, T0, Ct);

        var sent = await _broker.RequestAsync(from, to, "model-a", $$"""{"ask":"{{ask}}"}""", T0, conversation, Ct);

        return sent.Exchange!.Id;
    }

    [Fact]
    public async Task A_stale_question_closes_alone_and_its_conversation_stays_open()
    {
        var stale = await AskAsync(Orders, Person, "is it gross or net?");
        var other = await AskAsync(Orders, Person, "which rate?");

        Assert.NotNull(await _console.CloseRequestAsync(stale, "answered in the design doc", T0.AddMinutes(1), Ct));

        Assert.Equal("closed", _connection.ExecuteScalar<string>("SELECT outcome FROM exchanges WHERE id = @stale;", new { stale }));
        Assert.Null(_connection.ExecuteScalar<string>("SELECT outcome FROM exchanges WHERE id = @other;", new { other }));
        Assert.NotEqual("CLOSED", _connection.ExecuteScalar<string>("SELECT state FROM conversations WHERE id = 'pricing';"));
    }

    [Fact]
    public async Task The_agent_is_told_by_the_broker_with_the_reason_and_no_answer_is_written_for_the_person()
    {
        var stale = await AskAsync(Orders, Person, "is it gross or net?");

        await _console.CloseRequestAsync(stale, "answered in the design doc", T0.AddMinutes(1), Ct);

        var told = _connection.QuerySingle<(string Responder, string Payload)>(
            """
            SELECT responder_model AS Responder, payload_json AS Payload FROM messages
             WHERE exchange_id = @stale AND to_agent = 'orders@test' AND type = 'result';
            """,
            new { stale });

        Assert.Equal(Responder.Broker, told.Responder);
        Assert.Contains("closed this request without an answer", told.Payload, StringComparison.Ordinal);
        Assert.Contains("answered in the design doc", told.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_the_person_is_no_party_to_is_refused()
    {
        var theirs = await AskAsync(Orders, Billing, "is it gross or net?");

        Assert.Null(await _console.CloseRequestAsync(theirs, null, T0.AddMinutes(1), Ct));
        Assert.Equal("OPEN", _connection.ExecuteScalar<string>("SELECT state FROM exchanges WHERE id = @theirs;", new { theirs }));
    }

    [Fact]
    public async Task One_already_closed_or_unknown_changes_nothing()
    {
        var stale = await AskAsync(Orders, Person, "is it gross or net?");

        Assert.NotNull(await _console.CloseRequestAsync(stale, null, T0.AddMinutes(1), Ct));

        var messages = _connection.ExecuteScalar<long>("SELECT count(*) FROM messages;");

        Assert.Null(await _console.CloseRequestAsync(stale, null, T0.AddMinutes(2), Ct));
        Assert.Null(await _console.CloseRequestAsync(9_999, null, T0.AddMinutes(2), Ct));
        Assert.Equal(messages, _connection.ExecuteScalar<long>("SELECT count(*) FROM messages;"));
    }

    [Fact]
    public async Task Closing_two_in_the_same_minute_with_the_same_reason_is_not_taken_for_a_duplicate()
    {
        var first = await AskAsync(Orders, Person, "first?");
        var second = await AskAsync(Billing, Person, "second?", "tax");

        Assert.NotNull(await _console.CloseRequestAsync(first, "stale", T0.AddMinutes(1), Ct));
        Assert.NotNull(await _console.CloseRequestAsync(second, "stale", T0.AddMinutes(1), Ct));
    }
}
