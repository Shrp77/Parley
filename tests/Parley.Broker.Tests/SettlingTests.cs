using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// What a reply's <c>settles</c> does, and what it refuses (SPEC.md §5.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>The agent decides what the person's words settled</b>, because only the agent knows what its own questions were
/// for. It can close its own questions to the person, or take back the turn on the person's own request it had asked
/// back about — and nothing else, so it gains no authority over anybody.
/// </para>
/// <para>
/// <b>Every refusal asserts that nothing was stored</b>, not only that a refusal came back: the whole reply goes or none
/// of it does, and a refusal that left the Result written would close the person's request while telling the agent it
/// had not.
/// </para>
/// </remarks>
public sealed class SettlingTests : IDisposable
{
    private const string Conversation = "invoice-export";

    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Person = AgentName.Create("human-user@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;

    public SettlingTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-10-05T00:00:00.0000000Z');
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'claude-code', 'full', 'active'),
                ('billing@test', 'claude-code', 'full', 'active'),
                ('human-user@test', 'parley-console', 'pull', 'active');
            """);

        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.ServeFrom(T0);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> AskAsync(AgentName from, AgentName to, string ask, int minute, string conversation = Conversation)
    {
        var sent = await _broker.RequestAsync(
            from, to, Requests.Model, Requests.Request(ask), T0.AddMinutes(minute), conversation, Ct);

        Assert.True(sent.Exchange is not null, $"refused: {sent.Admission.Rejection}");

        return sent.Exchange!.Id;
    }

    private string OutcomeOf(long exchange) =>
        _connection.ExecuteScalar<string?>("SELECT outcome FROM exchanges WHERE id = @X;", new { X = exchange }) ?? "open";

    private string OwedBy(long exchange) =>
        _connection.ExecuteScalar<string>("SELECT owed_by FROM exchanges WHERE id = @X;", new { X = exchange }) ?? string.Empty;

    private long Messages() => _connection.ExecuteScalar<long>("SELECT count(*) FROM messages;");

    [Fact]
    public async Task A_result_to_the_person_settles_exactly_the_questions_it_names()
    {
        var first = await AskAsync(Orders, Person, "which invoice schema?", 1);
        var second = await AskAsync(Orders, Person, "gross or net?", 2);
        var line = await AskAsync(Person, Orders, "use schema 12", 3);

        var said = await _broker.ResultAsync(
            line, Orders, Requests.Model, Requests.Result(), T0.AddMinutes(4), Ct, settles: [first]);

        Assert.True(said.Succeeded, $"refused: {said.Error}");
        Assert.Equal([first], said.Settled);
        Assert.Empty(said.HandedBack);

        Assert.Equal("settled", OutcomeOf(first));
        Assert.Equal(line, _connection.ExecuteScalar<long>("SELECT settled_by FROM exchanges WHERE id = @X;", new { X = first }));
        Assert.Equal("open", OutcomeOf(second));
        Assert.Equal("complete", OutcomeOf(line));

        // No message is written on the settled question: the person did not answer there.
        Assert.Equal(0L, _connection.ExecuteScalar<long>(
            "SELECT count(*) FROM messages WHERE exchange_id = @X AND type <> 'request';", new { X = first }));
    }

    [Fact]
    public async Task A_clarify_to_the_person_can_settle_too()
    {
        // The agent may need to ask the person something back and say an earlier question is answered, in one move.
        var question = await AskAsync(Orders, Person, "which invoice schema?", 1);
        var line = await AskAsync(Person, Orders, "12, and how long will it take?", 2);

        var said = await _broker.ClarifyAsync(
            line, Orders, Requests.Model, Requests.Clarify("for which customers?"), T0.AddMinutes(3), Ct, settles: [question]);

        Assert.True(said.Succeeded, $"refused: {said.Error}");
        Assert.Equal("settled", OutcomeOf(question));
        Assert.Equal(Person.Value, OwedBy(line));
    }

    [Fact]
    public async Task The_persons_own_request_asked_back_about_is_handed_back_to_the_agent()
    {
        var persons = await AskAsync(Person, Orders, "export last month's invoices", 1);

        var asked = await _broker.ClarifyAsync(
            persons, Orders, Requests.Model, Requests.Clarify("which customers?"), T0.AddMinutes(2), Ct);

        Assert.True(asked.Succeeded);
        Assert.Equal(Person.Value, OwedBy(persons));

        var depth = _connection.ExecuteScalar<long>("SELECT clarify_depth FROM exchanges WHERE id = @X;", new { X = persons });
        var before = _connection.ExecuteScalar<long>("SELECT count(*) FROM messages WHERE exchange_id = @X;", new { X = persons });

        var line = await AskAsync(Person, Orders, "all of them, sorry", 3);

        var said = await _broker.ResultAsync(
            line, Orders, Requests.Model, Requests.Result(), T0.AddMinutes(4), Ct, settles: [persons]);

        Assert.True(said.Succeeded, $"refused: {said.Error}");
        Assert.Equal([persons], said.HandedBack);
        Assert.Empty(said.Settled);

        Assert.Equal(Orders.Value, OwedBy(persons));
        Assert.Equal("open", OutcomeOf(persons));
        Assert.Equal(depth, _connection.ExecuteScalar<long>("SELECT clarify_depth FROM exchanges WHERE id = @X;", new { X = persons }));
        Assert.Equal(before, _connection.ExecuteScalar<long>("SELECT count(*) FROM messages WHERE exchange_id = @X;", new { X = persons }));

        // And the agent can now answer it as usual.
        var answered = await _broker.ResultAsync(persons, Orders, Requests.Model, Requests.Result(), T0.AddMinutes(5), Ct);

        Assert.True(answered.Succeeded, $"refused: {answered.Error}");
    }

    [Fact]
    public async Task A_name_given_twice_is_settled_once()
    {
        var question = await AskAsync(Orders, Person, "which invoice schema?", 1);
        var line = await AskAsync(Person, Orders, "12", 2);

        var said = await _broker.ResultAsync(
            line, Orders, Requests.Model, Requests.Result(), T0.AddMinutes(3), Ct, settles: [question, question]);

        Assert.True(said.Succeeded, $"refused: {said.Error}");
        Assert.Equal([question], said.Settled);
    }

    [Fact]
    public async Task Naming_nothing_settles_nothing()
    {
        var question = await AskAsync(Orders, Person, "which invoice schema?", 1);
        var line = await AskAsync(Person, Orders, "what is it for?", 2);

        var said = await _broker.ResultAsync(line, Orders, Requests.Model, Requests.Result(), T0.AddMinutes(3), Ct);

        Assert.True(said.Succeeded);
        Assert.Empty(said.Settled);
        Assert.Equal("open", OutcomeOf(question));
    }

    [Fact]
    public async Task A_reply_not_on_the_persons_request_can_settle_nothing()
    {
        // Between agents a request is closed by its receiver's Result alone (§5.6): billing's request to orders is not
        // the person's line, so orders' reply to it cannot close orders' question to the person.
        var question = await AskAsync(Orders, Person, "which invoice schema?", 1);

        // billing joins the conversation by being asked something in it (§5.0).
        await AskAsync(Person, Billing, "are you in?", 2);
        var billings = await AskAsync(Billing, Orders, "gross or net?", 2);

        await AssertRefusedWholeAsync(
            () => _broker.ResultAsync(billings, Orders, Requests.Model, Requests.Result(), T0.AddMinutes(3), Ct, settles: [question]),
            billings,
            (question, SettleProblem.NotFromThePerson));

        Assert.Equal("open", OutcomeOf(question));
    }

    [Fact]
    public async Task Each_exchange_it_may_not_settle_is_named_with_why_and_nothing_is_stored()
    {
        var question = await AskAsync(Orders, Person, "which invoice schema?", 1);
        var elsewhere = await AskAsync(Orders, Person, "a different line of work", 2, "other-work");

        // billing joins the conversation by being asked something in it (§5.0), then asks the person in turn.
        await AskAsync(Person, Billing, "are you in?", 3);
        var billings = await AskAsync(Billing, Person, "and you?", 3);
        var betweenAgents = await AskAsync(Orders, Billing, "gross or net?", 3);
        var answered = await AskAsync(Orders, Person, "already answered", 4);

        Assert.True((await _broker.ResultAsync(
            answered, Person, Requests.Model, Requests.Result(), T0.AddMinutes(5), Ct)).Succeeded);

        // The person asked orders something, and it is orders' move on it: nothing to hand back.
        var notTheirMove = await AskAsync(Person, Orders, "how is the export going?", 6);
        var line = await AskAsync(Person, Orders, "use schema 12", 7);

        await AssertRefusedWholeAsync(
            () => _broker.ResultAsync(
                line, Orders, Requests.Model, Requests.Result(), T0.AddMinutes(8), Ct,
                settles: [question, elsewhere, billings, betweenAgents, answered, notTheirMove, line, 999_999]),
            line,
            (elsewhere, SettleProblem.OtherConversation),
            (billings, SettleProblem.NotBetweenYou),
            (betweenAgents, SettleProblem.NotBetweenYou),
            (answered, SettleProblem.NotOpen),
            (notTheirMove, SettleProblem.NotTheirMove),
            (line, SettleProblem.NotTheirMove),
            (999_999, SettleProblem.NotOpen));

        // The one it could have settled was not: the whole reply was refused.
        Assert.Equal("open", OutcomeOf(question));
    }

    [Fact]
    public async Task Racing_replies_settle_a_question_once_and_the_second_is_refused_whole()
    {
        var question = await AskAsync(Orders, Person, "which invoice schema?", 1);
        var firstLine = await AskAsync(Person, Orders, "12", 2);
        var secondLine = await AskAsync(Person, Orders, "12, I said", 3);

        var replies = await Task.WhenAll(
            _broker.ResultAsync(firstLine, Orders, Requests.Model, Requests.Result(), T0.AddMinutes(4), Ct, settles: [question]),
            _broker.ResultAsync(secondLine, Orders, Requests.Model, Requests.Result(), T0.AddMinutes(5), Ct, settles: [question]));

        Assert.Single(replies, r => r.Succeeded);
        var refused = Assert.Single(replies, r => !r.Succeeded);

        Assert.Equal(TransitionError.CannotSettle, refused.Error);
        Assert.Equal("settled", OutcomeOf(question));

        // Whichever lost, its own Result was not written either.
        Assert.Equal(1L, _connection.ExecuteScalar<long>(
            "SELECT count(*) FROM messages WHERE type = 'result' AND exchange_id IN (@A, @B);",
            new { A = firstLine, B = secondLine }));
    }

    [Fact]
    public async Task Sending_the_same_reply_again_is_refused_for_the_exchange_being_closed_not_for_settling()
    {
        // A Result sent twice finds its exchange closed by the first copy, and that is the refusal it gets:
        // the settles are judged only after the reply itself is admitted, so the second copy is never told that the
        // question its first copy settled "cannot be settled".
        var question = await AskAsync(Orders, Person, "which invoice schema?", 1);
        var line = await AskAsync(Person, Orders, "12", 2);
        var payload = Requests.Result();

        var first = await _broker.ResultAsync(line, Orders, Requests.Model, payload, T0.AddMinutes(3), Ct, settles: [question]);
        var again = await _broker.ResultAsync(line, Orders, Requests.Model, payload, T0.AddMinutes(3), Ct, settles: [question]);

        Assert.True(first.Succeeded);
        Assert.False(again.Succeeded);
        Assert.Equal(TransitionError.ExchangeNotOpen, again.Error);
    }

    [Fact]
    public async Task More_than_a_reply_may_name_is_an_argument_error()
    {
        var line = await AskAsync(Person, Orders, "12", 1);

        await Assert.ThrowsAsync<ArgumentException>(() => _broker.ResultAsync(
            line, Orders, Requests.Model, Requests.Result(), T0.AddMinutes(2), Ct,
            settles: [.. Enumerable.Range(1, Settling.MostNamed + 1).Select(i => (long)i)]));
    }

    private async Task AssertRefusedWholeAsync(
        Func<Task<SayOutcome>> reply,
        long repliedOn,
        params (long Exchange, SettleProblem Problem)[] expected)
    {
        var messages = Messages();
        var owed = OwedBy(repliedOn);

        var said = await reply();

        Assert.False(said.Succeeded);
        Assert.Equal(TransitionError.CannotSettle, said.Error);
        Assert.Equal(
            expected.OrderBy(e => e.Exchange),
            said.CannotSettle.OrderBy(e => e.Exchange));

        // Nothing stored: no message, and the exchange replied on is exactly as it was.
        Assert.Equal(messages, Messages());
        Assert.Equal(owed, OwedBy(repliedOn));
        Assert.Equal("open", OutcomeOf(repliedOn));
    }
}
