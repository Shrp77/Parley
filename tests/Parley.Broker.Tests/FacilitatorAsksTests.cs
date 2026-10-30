using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That the facilitator asks exactly the agents it should, once, and reads back exactly what they said (SPEC.md §10.2,
/// principle 2.11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven through the agent's own operations.</b> The answer is sent with <see cref="Broker.ResultAsync"/> and the
/// question back with <see cref="Broker.ClarifyAsync"/>, as an adapter sends them, so what the facilitator reads is what
/// an agent actually produces rather than a row a fixture wrote in the shape it expected.
/// </para>
/// <para>
/// Each "not asked" case names the rule it holds, and was watched to be asked with that rule removed from
/// <c>OwnershipQuestionRepository.DueAsync</c>.
/// </para>
/// </remarks>
[Integration]
public sealed class FacilitatorAsksTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly AgentName _parley;
    private readonly Facilitator _facilitator;
    private readonly Broker _agents;

    public FacilitatorAsksTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-09-28T00:00:00.0000000Z');
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'claude-code', 'full', 'active'),
                ('billing@test', 'claude-code', 'full', 'active'),
                ('inventory@test', 'claude-code', 'full', 'retired'),
                ('human-user@test', 'parley-console', 'pull', 'active'),
                ('orders@other', 'claude-code', 'full', 'active');
            """);

        _parley = new AgentRepository(_connection).EnsureFacilitatorAsync(ProjectName.Create("test")).GetAwaiter().GetResult();
        _facilitator = new Facilitator(_connection, BrokerOptions.Default);
        _agents = new Broker(_connection, BrokerOptions.Default);
        _agents.ServeFrom(T0);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_attached_agent_with_no_line_is_asked_once_in_a_conversation_of_its_own()
    {
        var asked = await _facilitator.AskDueAsync(_parley, ["orders@test"], T0, Ct);

        Assert.Equal([Orders], asked);

        var request = _connection.QuerySingle<(string From, string To, string Responder, string Payload, string Conversation)>(
            "SELECT from_agent, to_agent, responder_model, payload_json, conversation_id FROM messages;");

        Assert.Equal(_parley.Value, request.From);
        Assert.Equal(Orders.Value, request.To);
        Assert.Equal(Facilitation.Attribution, request.Responder);
        Assert.StartsWith("owns-orders-", request.Conversation, StringComparison.Ordinal);

        // Auto Accept is on by default, so the question says what will actually happen to the answer.
        Assert.Equal(Facilitation.AcceptedAsWritten, PayloadShape.Spoken(request.Payload));

        // And the second sweep asks nobody: the first question is still open.
        Assert.Empty(await _facilitator.AskDueAsync(_parley, ["orders@test"], T0.AddSeconds(5), Ct));
    }

    [Fact]
    public async Task Nobody_is_asked_who_should_not_be()
    {
        _connection.Execute(
            """
            INSERT INTO conversations (id, state, opened_utc) VALUES ('c-agreed', 'OPEN', '2026-09-28T00:00:00.0000000Z');
            INSERT INTO agent_ownership_accepted (agent, statement, source, proposed_length, conversation_id, accepted_utc)
            VALUES ('billing@test', 'invoices; not tax', 'proposed', 17, 'c-agreed', '2026-09-28T00:00:00.0000000Z');
            """);

        // billing has a line, inventory is retired, human-user is the person, the facilitator is the facilitator, and orders is
        // not attached: the only agent a question should reach is the one on another machine.
        var asked = await _facilitator.AskDueAsync(
            _parley, ["billing@test", "inventory@test", "human-user@test", "orders@other", _parley.Value], T0, Ct);

        Assert.Equal([AgentName.Create("orders@other")], asked);

        // Retirement is refused again when the Request is admitted, so the sweep above would pass without its own rule.
        // Asked of the rule directly, so a sweep that tried every retired agent on every tick is visible here.
        Assert.Empty(await new OwnershipQuestionRepository(_connection).DueAsync(["inventory@test"], cancellationToken: Ct));
    }

    [Fact]
    public async Task A_discarded_answer_is_not_asked_again_by_the_sweep()
    {
        // Having been asked at all stops automatic asking (SPEC.md §10.2). Re-asking a discarded answer on the next
        // tick would overrule the person who discarded it.
        var exchange = (await _facilitator.AskAsync(_parley, Orders, T0, Ct)).Exchange!.Value;
        await Answer(exchange, Orders, "orders.");
        _ = await _facilitator.ReadBackAsync(_parley, T0, Ct);
        _ = await new OwnershipQuestionRepository(_connection).DiscardAsync(Orders, T0, cancellationToken: Ct);

        Assert.Empty(await _facilitator.AskDueAsync(_parley, ["orders@test"], T0.AddMinutes(1), Ct));
    }

    [Fact]
    public async Task An_answer_is_read_back_whole_and_as_a_line()
    {
        var exchange = (await _facilitator.AskAsync(_parley, Orders, T0, Ct)).Exchange!.Value;

        await Answer(exchange, Orders, "1. Orders and their totals.\n\n2. Not tax, which billing owns.");

        var answer = Assert.Single(await _facilitator.ReadBackAsync(_parley, T0, Ct));

        Assert.Equal(Orders, answer.Agent);
        Assert.Equal("1. Orders and their totals.\n\n2. Not tax, which billing owns.", answer.Answer);
        Assert.Equal("1. Orders and their totals. 2. Not tax, which billing owns.", answer.Line);
        Assert.False(answer.Blocked);

        // Read once: a second read finds nothing new, only the same answer while nothing has decided it (lens 1 run 91),
        // and nothing once something has.
        Assert.Equal(answer.Question, Assert.Single(await _facilitator.ReadBackAsync(_parley, T0, Ct)).Question);
        Assert.True(await new OwnershipQuestionRepository(_connection).WaitAsync(answer.Question, "held", cancellationToken: Ct));
        Assert.Empty(await _facilitator.ReadBackAsync(_parley, T0, Ct));
    }

    [Fact]
    public async Task A_question_back_waits_for_a_person_and_is_not_an_answer()
    {
        var exchange = (await _facilitator.AskAsync(_parley, Orders, T0, Ct)).Exchange!.Value;

        var clarified = await _agents.ClarifyAsync(
            exchange, Orders, "some-model-9", Json("text", "Do you mean this repository or the service?"), T0, Ct);
        Assert.True(clarified.Succeeded);

        Assert.Empty(await _facilitator.ReadBackAsync(_parley, T0, Ct));
        Assert.Equal("asked_back", _connection.ExecuteScalar<string>("SELECT state FROM ownership_questions;"));
    }

    [Fact]
    public async Task An_agents_own_owns_conversation_is_not_an_answer_to_anything()
    {
        // billing opens a conversation under the prefix and orders "answers" it; neither is the
        // facilitator's, so nothing is read back and orders is still due.
        var sent = await _agents.RequestAsync(
            Billing, Orders, "some-model-9", Json("ask", "What do you own?"), T0, "owns-orders-20260928-120000", Ct);
        await Answer(sent.Exchange!.Id, Orders, "everything");

        Assert.Empty(await _facilitator.ReadBackAsync(_parley, T0, Ct));
        Assert.Equal([Orders], await _facilitator.AskDueAsync(_parley, ["orders@test"], T0, Ct));
    }

    [Fact]
    public async Task Two_agents_of_one_role_asked_in_one_second_get_two_conversations()
    {
        var asked = await _facilitator.AskDueAsync(_parley, ["orders@test", "orders@other"], T0, Ct);

        Assert.Equal(2, asked.Count);
        Assert.Equal(
            2, _connection.ExecuteScalar<long>("SELECT count(DISTINCT conversation_id) FROM ownership_questions;"));
    }

    [Fact]
    public async Task Accepting_automatically_records_the_line_settles_the_question_and_closes_the_conversation()
    {
        var exchange = (await _facilitator.AskAsync(_parley, Orders, T0, Ct)).Exchange!.Value;
        await Answer(exchange, Orders, "Orders and totals.\nNot tax.");

        var answer = Assert.Single(await _facilitator.ReadBackAsync(_parley, T0, Ct));

        Assert.True(await _facilitator.AcceptAutomaticallyAsync(_parley, answer, T0, Ct));

        var line = _connection.QuerySingle<(string Statement, string Source, long Length, string Conversation)>(
            "SELECT statement, source, proposed_length, conversation_id FROM agent_ownership_accepted;");

        Assert.Equal("Orders and totals. Not tax.", line.Statement);
        Assert.Equal("automatic", line.Source);

        // The agent's own message, never the line: the evidence is what agents write.
        Assert.Equal("Orders and totals.\nNot tax.".Length, line.Length);
        Assert.Equal("accepted", _connection.ExecuteScalar<string>("SELECT state FROM ownership_questions;"));
        Assert.Equal(
            "CLOSED",
            _connection.ExecuteScalar<string>("SELECT state FROM conversations WHERE id = @Id;", new { Id = line.Conversation }));
    }

    [Fact]
    public async Task A_refresh_is_due_once_for_the_first_automatic_line_and_never_for_a_later_one()
    {
        // SPEC.md §11.1: a restart costs what the agent has learned, which is almost nothing just after it first attached
        // and a great deal later. So the first automatic line is refreshed and every later change waits.
        var first = (await _facilitator.AskAsync(_parley, Orders, T0, Ct)).Exchange!.Value;
        await Answer(first, Orders, "Orders.");
        Assert.True(await _facilitator.AcceptAutomaticallyAsync(
            _parley, Assert.Single(await _facilitator.ReadBackAsync(_parley, T0, Ct)), T0, Ct));

        var due = Assert.Single(await _facilitator.RefreshDueAsync(Ct));
        Assert.Equal(Orders, due.Agent);
        Assert.True(await _facilitator.RefreshSentAsync(due.Question, Ct));
        Assert.Empty(await _facilitator.RefreshDueAsync(Ct));

        // Asked again later — as a person would ask it — and accepted automatically again: no second refresh.
        var second = (await _facilitator.AskAsync(_parley, Orders, T0.AddHours(1), Ct)).Exchange!.Value;
        await Answer(second, Orders, "Orders and returns.");
        Assert.True(await _facilitator.AcceptAutomaticallyAsync(
            _parley, Assert.Single(await _facilitator.ReadBackAsync(_parley, T0, Ct)), T0.AddHours(1), Ct));

        Assert.Empty(await _facilitator.RefreshDueAsync(Ct));
    }

    [Fact]
    public async Task A_line_a_person_corrected_before_the_refresh_is_not_refreshed_for()
    {
        // The refresh is for the automatic line. A person who changed it first has made it theirs, and a refresh for
        // a person's line is a later change like any other.
        var exchange = (await _facilitator.AskAsync(_parley, Orders, T0, Ct)).Exchange!.Value;
        await Answer(exchange, Orders, "Orders.");
        var answer = Assert.Single(await _facilitator.ReadBackAsync(_parley, T0, Ct));
        Assert.True(await _facilitator.AcceptAutomaticallyAsync(_parley, answer, T0, Ct));

        _ = await new AgentRepository(_connection).RecordOwnershipAsync(
            Orders,
            new OwnershipAcceptance("Orders and carts.", OwnershipSource.Edited, 7, answer.Conversation),
            T0.AddMinutes(1),
            cancellationToken: Ct);

        Assert.Empty(await _facilitator.RefreshDueAsync(Ct));
    }

    [Fact]
    public async Task A_conversation_a_person_closed_before_the_answer_settles_the_question_unanswered()
    {
        var asked = await _facilitator.AskAsync(_parley, Orders, T0, Ct);

        await new ConversationRepository(_connection).ForceConcludeAsync(asked.Conversation!, "not now", T0, cancellationToken: Ct);

        Assert.Empty(await _facilitator.ReadBackAsync(_parley, T0, Ct));
        Assert.Equal("discarded", _connection.ExecuteScalar<string>("SELECT state FROM ownership_questions;"));
    }

    private async Task Answer(long exchange, AgentName from, string text)
    {
        var payload = new JsonObject
        {
            ["outcomes"] = new JsonArray(new JsonObject
            {
                ["id"] = PayloadShape.WholeRequest,
                ["outcome"] = "answered",
                ["text"] = text
            })
        }.ToJsonString();

        Assert.True((await _agents.ResultAsync(exchange, from, "some-model-9", payload, T0, Ct)).Succeeded);
    }

    private static string Json(string field, string text) => new JsonObject { [field] = text }.ToJsonString();
}
