using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That a reply a person writes to an agent that asked Parley something back is recorded as the person's, never as
/// Parley's (SPEC.md §5, FR-003a), and the decisions a person makes about a question hold their rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reply travels under Parley's registry name</b>, because only the requester may answer a Clarify on its own
/// exchange (§5.2). So the one thing that says whose words they are is the attribution, and a reply stored as
/// <c>parley:facilitator</c> would put a person's sentence in Parley's mouth in every transcript and every inbox —
/// §10.2's warning about <c>instruct</c>, reached from the other side. Watched to fail against a reply attributed
/// <c>parley:facilitator</c>.
/// </para>
/// </remarks>
public sealed class APersonsWordsAreNeverParleysTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly AgentName _parley;
    private readonly Facilitator _facilitator;
    private readonly Broker _agents;

    public APersonsWordsAreNeverParleysTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-09-28T00:00:00.0000000Z');
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'claude-code', 'full', 'active'),
                ('inventory@test', 'claude-code', 'full', 'retired'),
                ('arvind@test', 'parley-console', 'pull', 'active');
            """);

        _parley = new AgentRepository(_connection).EnsureFacilitatorAsync("test").GetAwaiter().GetResult();
        _facilitator = new Facilitator(_connection, BrokerOptions.Default);
        _agents = new Broker(_connection, BrokerOptions.Default);
        _agents.ServeFrom(T0);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reply_from_the_review_is_stored_as_a_persons_words_and_hands_the_turn_back()
    {
        var exchange = await AskedBackAsync();

        var replied = await _facilitator.ReplyAsync(_parley, Orders, "This repository, the orders service.", T0, Ct);

        Assert.True(replied.Done, replied.Problem);

        var reply = _connection.QuerySingle<(string From, string To, string Responder, string Type)>(
            "SELECT from_agent, to_agent, responder_model, type FROM messages WHERE exchange_id = @X ORDER BY id DESC LIMIT 1;",
            new { X = exchange });

        Assert.Equal(_parley.Value, reply.From);
        Assert.Equal(Orders.Value, reply.To);
        Assert.Equal("clarify", reply.Type);
        Assert.Equal(Facilitation.PersonAttribution, reply.Responder);

        // The turn is the agent's again, and the question is asked again, waiting on it.
        Assert.Equal(Orders.Value, _connection.ExecuteScalar<string>("SELECT owed_by FROM exchanges WHERE id = @X;", new { X = exchange }));
        Assert.Equal("asked", _connection.ExecuteScalar<string>("SELECT state FROM ownership_questions;"));
    }

    [Fact]
    public async Task There_is_nothing_to_reply_to_until_the_agent_asks_back()
    {
        _ = await _facilitator.AskAsync(_parley, Orders, T0, Ct);

        Assert.False((await _facilitator.ReplyAsync(_parley, Orders, "hello", T0, Ct)).Done);
        Assert.Equal(1L, _connection.ExecuteScalar<long>("SELECT count(*) FROM messages;"));
    }

    [Fact]
    public async Task A_reply_is_held_to_the_message_cap()
    {
        _ = await AskedBackAsync();

        var refused = await _facilitator.ReplyAsync(
            _parley, Orders, new string('x', MessageLimits.MaxPayloadCharacters + 1), T0, Ct);

        Assert.False(refused.Done);
        Assert.Contains(MessageLimits.MaxPayloadCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
            refused.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_again_is_refused_for_a_seat_a_retired_agent_and_an_undecided_answer()
    {
        Assert.False((await _facilitator.AskAgainAsync(_parley, AgentName.Create("arvind@test"), T0, Ct)).Done);
        Assert.False((await _facilitator.AskAgainAsync(_parley, AgentName.Create("inventory@test"), T0, Ct)).Done);
        Assert.False((await _facilitator.AskAgainAsync(_parley, _parley, T0, Ct)).Done);

        Assert.True((await _facilitator.AskAgainAsync(_parley, Orders, T0, Ct)).Done);

        // The first question is still open, so a second would lose whatever it is about to answer.
        Assert.False((await _facilitator.AskAgainAsync(_parley, Orders, T0.AddMinutes(1), Ct)).Done);
        Assert.Equal(1L, _connection.ExecuteScalar<long>("SELECT count(*) FROM ownership_questions;"));
    }

    [Fact]
    public async Task Only_a_waiting_answer_can_be_discarded_and_discarding_it_writes_nothing()
    {
        var exchange = (await _facilitator.AskAsync(_parley, Orders, T0, Ct)).Exchange!.Value;

        Assert.False((await _facilitator.DiscardAsync(_parley, Orders, T0, Ct)).Done);

        await AnswerAsync(exchange, "Orders.");
        var answer = Assert.Single(await _facilitator.ReadBackAsync(_parley, T0, Ct));
        Assert.True(await _facilitator.HoldAsync(answer, "Auto Accept is off for this project.", Ct));

        Assert.True((await _facilitator.DiscardAsync(_parley, Orders, T0, Ct)).Done);

        Assert.Equal(0L, _connection.ExecuteScalar<long>("SELECT count(*) FROM agent_ownership_accepted;"));
        Assert.Equal("discarded", _connection.ExecuteScalar<string>("SELECT state FROM ownership_questions;"));
        Assert.Equal("CLOSED", _connection.ExecuteScalar<string>(
            "SELECT state FROM conversations WHERE id = @Id;", new { Id = answer.Conversation }));
    }

    [Fact]
    public async Task A_person_recording_a_line_settles_the_waiting_answer_and_closes_its_conversation()
    {
        var exchange = (await _facilitator.AskAsync(_parley, Orders, T0, Ct)).Exchange!.Value;
        await AnswerAsync(exchange, "Orders.");
        var answer = Assert.Single(await _facilitator.ReadBackAsync(_parley, T0, Ct));
        Assert.True(await _facilitator.HoldAsync(answer, "Auto Accept is off for this project.", Ct));

        var (recorded, _) = await new AgentRepository(_connection).RecordOwnershipAsync(
            Orders, new OwnershipAcceptance("Orders.", OwnershipSource.Proposed, 7, answer.Conversation), T0,
            cancellationToken: Ct);
        Assert.True(recorded);

        Assert.True(await _facilitator.SettledByPersonAsync(_parley, Orders, T0, cancellationToken: Ct));
        Assert.Equal("accepted", _connection.ExecuteScalar<string>("SELECT state FROM ownership_questions;"));
        Assert.Equal("CLOSED", _connection.ExecuteScalar<string>(
            "SELECT state FROM conversations WHERE id = @Id;", new { Id = answer.Conversation }));
    }

    [Fact]
    public async Task A_person_writing_a_line_before_Parley_read_an_answer_settles_the_question_without_one()
    {
        // Found by rewriting 020's discussion test: a person accepting from the transcript, or writing a line, inside the
        // few seconds before Parley's next pass read the agent's answer, settled the question as accepted with no answer
        // -- which storage refuses, so the person's own record failed. The line overtook the question; it is discarded.
        _ = await _facilitator.AskAsync(_parley, Orders, T0, Ct);

        var (recorded, _) = await new AgentRepository(_connection).RecordOwnershipAsync(
            Orders, new OwnershipAcceptance("Orders.", OwnershipSource.Composed, null, null), T0, cancellationToken: Ct);
        Assert.True(recorded);

        Assert.True(await _facilitator.SettledByPersonAsync(_parley, Orders, T0, cancellationToken: Ct));
        Assert.Equal("discarded", _connection.ExecuteScalar<string>("SELECT state FROM ownership_questions;"));

        // And the agent, still holding the question, is not polled: nothing asks it to spend a turn on a vote.
        Assert.Equal(0L, _connection.ExecuteScalar<long>(
            "SELECT count(*) FROM conversation_participants WHERE polled_utc IS NOT NULL;"));
    }

    [Fact]
    public async Task Turning_the_switch_on_leaves_a_waiting_answer_waiting()
    {
        var exchange = (await _facilitator.AskAsync(_parley, Orders, T0, Ct)).Exchange!.Value;
        await AnswerAsync(exchange, "Orders.");
        Assert.True(await _facilitator.HoldAsync(
            Assert.Single(await _facilitator.ReadBackAsync(_parley, T0, Ct)), "Auto Accept is off for this project.", Ct));

        Assert.True(await _facilitator.SetAutoAcceptAsync(on: true, T0, Ct));

        Assert.Equal("waiting", _connection.ExecuteScalar<string>("SELECT state FROM ownership_questions;"));
        Assert.Equal(0L, _connection.ExecuteScalar<long>("SELECT count(*) FROM agent_ownership_accepted;"));
    }

    private async Task<long> AskedBackAsync()
    {
        var exchange = (await _facilitator.AskAsync(_parley, Orders, T0, Ct)).Exchange!.Value;

        Assert.True((await _agents.ClarifyAsync(
            exchange, Orders, "some-model-9", new JsonObject { ["text"] = "Which repository do you mean?" }.ToJsonString(),
            T0, Ct)).Succeeded);

        Assert.Empty(await _facilitator.ReadBackAsync(_parley, T0, Ct));

        return exchange;
    }

    private async Task AnswerAsync(long exchange, string text)
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

        Assert.True((await _agents.ResultAsync(exchange, Orders, "some-model-9", payload, T0, Ct)).Succeeded);
    }
}
