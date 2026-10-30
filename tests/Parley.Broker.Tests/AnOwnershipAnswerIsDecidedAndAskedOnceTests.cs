using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That an agent's answer to the ownership question is decided even when the pass that read it stopped, and that destroying
/// the question's conversation does not make Parley ask again by itself (SPEC.md §10.2).
/// </summary>
/// <remarks>
/// Both found by review, lens 1 run 91. A pass records an answer before deciding it, and a pass stopped in between left it
/// answered for ever, with asking again and discarding both refused. And a destroy took the question's row with it, so the
/// next pass asked again, though having been asked at all stops asking.
/// </remarks>
[Integration]
public sealed class AnOwnershipAnswerIsDecidedAndAskedOnceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly AgentName _parley;
    private readonly Facilitator _facilitator;
    private readonly Broker _agents;

    public AnOwnershipAnswerIsDecidedAndAskedOnceTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-09-28T00:00:00.0000000Z');
            INSERT INTO agents (name, adapter, tier, status) VALUES ('orders@test', 'claude-code', 'full', 'active');
            """);

        _parley = new AgentRepository(_connection).EnsureFacilitatorAsync(ProjectName.Create("test")).GetAwaiter().GetResult();
        _facilitator = new Facilitator(_connection, BrokerOptions.Default);
        _agents = new Broker(_connection, BrokerOptions.Default);
        _agents.ServeFrom(T0);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_answer_a_stopped_pass_read_and_never_decided_is_handed_to_the_next_pass()
    {
        var asked = await _facilitator.AskAsync(_parley, Orders, T0, Ct);
        await Answer(asked.Exchange!.Value, "orders and their fulfilment");

        var first = Assert.Single(await _facilitator.ReadBackAsync(_parley, T0, Ct));

        // The pass stopped here: nothing accepted it and nothing held it.
        var again = Assert.Single(await _facilitator.ReadBackAsync(_parley, T0.AddMinutes(1), Ct));
        Assert.Equal(first.Question, again.Question);
        Assert.Equal("orders and their fulfilment", again.Line);

        // Once decided, it is not handed over again.
        Assert.True(await new OwnershipQuestionRepository(_connection).WaitAsync(again.Question, "held", cancellationToken: Ct));
        Assert.Empty(await _facilitator.ReadBackAsync(_parley, T0.AddMinutes(2), Ct));
    }

    [Fact]
    public async Task Destroying_the_questions_conversation_does_not_make_the_sweep_ask_again()
    {
        var asked = await _facilitator.AskAsync(_parley, Orders, T0, Ct);
        await Answer(asked.Exchange!.Value, "orders.");
        _ = await _facilitator.ReadBackAsync(_parley, T0, Ct);
        _ = await new OwnershipQuestionRepository(_connection).DiscardAsync(Orders, T0, cancellationToken: Ct);

        _ = await new ConversationRepository(_connection).DestroyAsync(asked.Conversation!, T0.AddMinutes(1), cancellationToken: Ct);

        Assert.Empty(await _facilitator.AskDueAsync(_parley, [Orders.Value], T0.AddMinutes(2), Ct));
    }

    [Fact]
    public async Task A_repository_attaching_under_a_purged_name_is_a_new_agent_and_is_asked()
    {
        // Lens 1 run 92: the purge left a tombstone naming the agent, and reading tombstones as "asked" never asked
        // the new one.
        _ = await _facilitator.AskAsync(_parley, Orders, T0, Ct);
        Assert.Equal(PurgeVerdict.Gone, (await new AgentRepository(_connection).PurgeAsync(Orders, T0.AddMinutes(1), cancellationToken: Ct)).Verdict);

        _connection.Execute("INSERT INTO agents (name, adapter, tier, status) VALUES ('orders@test', 'claude-code', 'full', 'active');");

        Assert.Single(await _facilitator.AskDueAsync(_parley, [Orders.Value], T0.AddMinutes(2), Ct));
    }

    [Fact]
    public async Task An_agent_named_in_a_destroyed_conversation_it_was_never_asked_in_is_still_asked()
    {
        // Lens 1 run 92: an agent drawn into another agent's owns- conversation is in its tombstone, and was never asked.
        _connection.Execute(
            """
            INSERT INTO agents (name, adapter, tier, status) VALUES ('inventory@test', 'claude-code', 'full', 'active');
            INSERT INTO destroyed_conversations (id, destroyed_utc, messages, participants)
            VALUES ('owns-orders-20260928-120000', '2026-09-28T12:00:00.0000000Z', 3,
                    'inventory@test, orders@test, parley-facilitator@test');
            """);

        Assert.Single(await _facilitator.AskDueAsync(_parley, ["inventory@test"], T0, Ct));
    }

    [Fact]
    public async Task The_first_asking_is_marked_on_the_agents_row_and_a_later_one_does_not_move_it()
    {
        var asked = await _facilitator.AskAsync(_parley, Orders, T0, Ct);
        await Answer(asked.Exchange!.Value, "orders.");
        _ = await _facilitator.ReadBackAsync(_parley, T0, Ct);
        _ = await new OwnershipQuestionRepository(_connection).DiscardAsync(Orders, T0, cancellationToken: Ct);
        _ = await _facilitator.AskAgainAsync(_parley, Orders, T0.AddHours(1), Ct);

        Assert.Equal(2L, _connection.ExecuteScalar<long>("SELECT count(*) FROM ownership_questions WHERE agent = 'orders@test';"));
        Assert.Equal(
            ExchangeRow.FormatTimestamp(T0),
            _connection.ExecuteScalar<string>("SELECT asked_utc FROM agents WHERE name = 'orders@test';"));
    }

    private async Task Answer(long exchange, string text)
    {
        var payload = new JsonObject
        {
            ["outcomes"] = new JsonArray(new JsonObject
            {
                ["id"] = PayloadShape.WholeRequest,
                ["outcome"] = "answered",
                ["text"] = text,
            }),
        }.ToJsonString();

        Assert.True((await _agents.ResultAsync(exchange, Orders, "some-model-9", payload, T0, Ct)).Succeeded);
    }
}
