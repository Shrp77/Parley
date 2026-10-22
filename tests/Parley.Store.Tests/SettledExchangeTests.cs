using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That storage holds the rules of a settled question (SPEC.md §5.6, §9.3): only an agent's own question to the person,
/// only by that agent, and only beside a reply it wrote at that instant on a Request the person opened with it in the
/// same conversation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Direct SQL for the refusals</b>, for the reason <see cref="PersonSeatStoreTests"/> gives: these are about what
/// storage refuses, and a repository that checked first would make a missing rule invisible here.
/// </para>
/// <para>
/// <b>Each refusal was watched to fail</b> against the schema with its rule taken out, one rule at a time.
/// </para>
/// </remarks>
public sealed class SettledExchangeTests : IDisposable
{
    private static readonly ProjectName Test = ProjectName.Create("test");
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    private AgentName _person;

    public SettledExchangeTests()
    {
        _person = new AgentRepository(_connection).EnsurePersonAsync(Test, cancellationToken: CancellationToken.None)
            .GetAwaiter().GetResult();

        Work.Register(_connection, Orders, Billing);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string At(DateTimeOffset when) => ExchangeRow.FormatTimestamp(when);

    // orders asked the person something, and the person later opened a Request with orders: the shape §5.6 settles.
    private async Task<(Exchange Question, Exchange Settling)> AskedThenToldAsync(string conversation = Work.Conversation)
    {
        var (question, _) = await Work.OpenAsync(
            _connection, Orders, _person, T0, Work.Request("which invoice schema?"), conversation, cancellationToken: Ct);
        var (settling, _) = await Work.OpenAsync(
            _connection, _person, Orders, T0.AddMinutes(1), Work.Request("use 12"), conversation, cancellationToken: Ct);

        return (question, settling);
    }

    // orders replies on the person's Request, at the instant the settle will carry.
    private async Task ReplyAsync(Exchange settling, AgentName from, DateTimeOffset at, MessageType type = MessageType.Result)
    {
        var to = from == settling.Requester ? settling.Receiver : settling.Requester;
        var payload = type == MessageType.Result
            ? Work.Result(("request", "answered"))
            : new System.Text.Json.Nodes.JsonObject { ["text"] = "which version?" }.ToJsonString();

        await new MessageRepository(_connection).AppendAsync(
            settling.Id, type, from, to, Work.Model, payload, at, cancellationToken: Ct);
    }

    private void Settle(long question, long settledBy, DateTimeOffset at) =>
        _connection.Execute(
            "UPDATE exchanges SET state = 'CLOSED', closed_utc = @At, outcome = 'settled', settled_by = @By WHERE id = @Id;",
            new { At = At(at), By = settledBy, Id = question });

    [Fact]
    public async Task An_agent_settles_its_own_question_to_the_person_beside_its_reply()
    {
        var (question, settling) = await AskedThenToldAsync();
        var at = T0.AddMinutes(2);

        await ReplyAsync(settling, Orders, at);

        Assert.True(question.Settle(Orders, settling.Id, at).Succeeded);
        Assert.True(await new ExchangeRepository(_connection).SaveAsync(question, cancellationToken: Ct));

        var read = await new ExchangeRepository(_connection).GetAsync(question.Id, cancellationToken: Ct);

        Assert.Equal(ExchangeOutcome.Settled, read!.Outcome);
        Assert.Equal(settling.Id, read.SettledBy);

        var snapshot = Assert.Single(
            await new ExchangeRepository(_connection).GetSnapshotsAsync(Work.Conversation, Ct),
            s => s.ExchangeId == question.Id);

        Assert.Equal(settling.Id, snapshot.SettledBy);
    }

    [Fact]
    public async Task A_clarify_settles_as_well_as_a_result()
    {
        var (question, settling) = await AskedThenToldAsync();
        var at = T0.AddMinutes(2);

        await ReplyAsync(settling, Orders, at, MessageType.Clarify);

        Settle(question.Id, settling.Id, at);

        Assert.Equal("settled", _connection.ExecuteScalar<string>(
            "SELECT outcome FROM exchanges WHERE id = @Id;", new { question.Id }));
    }

    [Fact]
    public async Task Settled_without_what_settled_it_is_refused()
    {
        var (question, _) = await AskedThenToldAsync();

        Assert.Throws<SqliteException>(() => _connection.Execute(
            "UPDATE exchanges SET state = 'CLOSED', closed_utc = @At, outcome = 'settled' WHERE id = @Id;",
            new { At = At(T0), question.Id }));
    }

    [Fact]
    public async Task What_settled_it_is_refused_on_an_open_exchange()
    {
        // The CHECK reads `outcome IS 'settled'`, not `=`: with `=`, an open exchange's NULL outcome made the comparison
        // NULL and a CHECK lets NULL through, so settled_by could be written on an open question.
        var (question, settling) = await AskedThenToldAsync();

        Assert.Throws<SqliteException>(() => _connection.Execute(
            "UPDATE exchanges SET settled_by = @By WHERE id = @Id;", new { By = settling.Id, question.Id }));
    }

    [Fact]
    public async Task What_settled_it_is_refused_beside_any_other_outcome()
    {
        var (question, settling) = await AskedThenToldAsync();

        await Work.CloseAsync(_connection, question, T0.AddMinutes(2), cancellationToken: Ct);

        Assert.Throws<SqliteException>(() => _connection.Execute(
            "UPDATE exchanges SET settled_by = @By WHERE id = @Id;", new { By = settling.Id, question.Id }));
    }

    [Fact]
    public async Task An_exchange_cannot_settle_itself()
    {
        var (question, _) = await AskedThenToldAsync();

        Assert.Throws<SqliteException>(() => Settle(question.Id, question.Id, T0));
    }

    [Fact]
    public async Task A_reply_in_another_conversation_settles_nothing()
    {
        var (question, _) = await AskedThenToldAsync();
        var (elsewhere, _) = await Work.OpenAsync(
            _connection, _person, Orders, T0.AddMinutes(1), Work.Request("unrelated"), "other-work", cancellationToken: Ct);
        var at = T0.AddMinutes(2);

        await ReplyAsync(elsewhere, Orders, at);

        Assert.Throws<SqliteException>(() => Settle(question.Id, elsewhere.Id, at));
    }

    [Fact]
    public async Task A_reply_between_other_parties_settles_nothing()
    {
        // billing's reply to the person cannot settle orders' question.
        var (question, _) = await AskedThenToldAsync();
        var (billings, _) = await Work.OpenAsync(
            _connection, _person, Billing, T0.AddMinutes(1), Work.Request("and you?"), cancellationToken: Ct);
        var at = T0.AddMinutes(2);

        await ReplyAsync(billings, Billing, at);

        Assert.Throws<SqliteException>(() => Settle(question.Id, billings.Id, at));
    }

    [Fact]
    public async Task A_reply_on_another_agents_request_settles_nothing()
    {
        // Only the requester check is wrong here: the reply is orders', on a Request orders received, in this
        // conversation -- but billing opened it, not the person.
        var (question, _) = await AskedThenToldAsync();
        var (billings, _) = await Work.OpenAsync(
            _connection, Billing, Orders, T0.AddMinutes(1), Work.Request("gross or net?"), cancellationToken: Ct);
        var at = T0.AddMinutes(2);

        await ReplyAsync(billings, Orders, at);

        Assert.Throws<SqliteException>(() => Settle(question.Id, billings.Id, at));
    }

    [Fact]
    public async Task A_reply_on_the_persons_request_to_somebody_else_settles_nothing()
    {
        // Only the receiver check is wrong here: the person opened it and orders wrote a message on it at that instant,
        // which no broker would write -- but it is billing's Request, not orders'. Written raw, because storage is what
        // is being asked.
        var (question, _) = await AskedThenToldAsync();
        var (toBilling, _) = await Work.OpenAsync(
            _connection, _person, Billing, T0.AddMinutes(1), Work.Request("and you?"), cancellationToken: Ct);
        var at = T0.AddMinutes(2);

        await new MessageRepository(_connection).AppendAsync(
            toBilling.Id, MessageType.Result, Orders, _person, Work.Model, Work.Result(("request", "answered")), at,
            cancellationToken: Ct);

        Assert.Throws<SqliteException>(() => Settle(question.Id, toBilling.Id, at));
    }

    [Fact]
    public async Task Only_a_question_to_the_person_can_be_settled()
    {
        // Between agents a Request is closed by its receiver's Result alone (§5.6).
        var (question, _) = await Work.OpenAsync(
            _connection, Orders, Billing, T0, Work.Request("gross or net?"), cancellationToken: Ct);
        var (answer, _) = await Work.OpenAsync(
            _connection, Billing, Orders, T0.AddMinutes(1), Work.Request("net"), cancellationToken: Ct);
        var at = T0.AddMinutes(2);

        await ReplyAsync(answer, Orders, at);

        Assert.Throws<SqliteException>(() => Settle(question.Id, answer.Id, at));
    }

    [Fact]
    public async Task The_persons_own_request_is_never_settled()
    {
        // It is handed back instead (§5.6). As settled, its receiver would be an agent, never the person.
        var (_, settling) = await AskedThenToldAsync();
        var (persons, _) = await Work.OpenAsync(
            _connection, _person, Orders, T0.AddMinutes(1), Work.Request("and the floor?"), cancellationToken: Ct);
        var at = T0.AddMinutes(2);

        await ReplyAsync(settling, Orders, at);

        Assert.Throws<SqliteException>(() => Settle(persons.Id, settling.Id, at));
    }

    [Fact]
    public async Task A_settle_with_no_reply_at_that_instant_is_refused()
    {
        // The instant is what lets a surface put the list beside the reply; a settle no reply stands beside is refused.
        var (question, settling) = await AskedThenToldAsync();

        await ReplyAsync(settling, Orders, T0.AddMinutes(2));

        Assert.Throws<SqliteException>(() => Settle(question.Id, settling.Id, T0.AddMinutes(3)));
    }

    [Fact]
    public async Task Only_the_agents_own_reply_settles()
    {
        // A message from the person on that exchange at that instant is not the agent saying anything was settled.
        var (question, settling) = await AskedThenToldAsync();
        var at = T0.AddMinutes(2);

        await ReplyAsync(settling, Orders, T0.AddMinutes(1).AddSeconds(30), MessageType.Clarify);
        await ReplyAsync(settling, _person, at, MessageType.Clarify);

        Assert.Throws<SqliteException>(() => Settle(question.Id, settling.Id, at));
    }

    [Fact]
    public async Task What_an_agent_could_settle_is_its_open_questions_to_the_person_in_that_conversation_oldest_first()
    {
        var repository = new ExchangeRepository(_connection);
        var (first, _) = await Work.OpenAsync(
            _connection, Orders, _person, T0, Work.Request("first"), cancellationToken: Ct);
        var (second, _) = await Work.OpenAsync(
            _connection, Orders, _person, T0.AddMinutes(1), Work.Request("second"), cancellationToken: Ct);

        // Not listed: another agent's, another conversation's, and one the person already answered.
        await Work.OpenAsync(_connection, Billing, _person, T0, Work.Request("billing's"), cancellationToken: Ct);
        await Work.OpenAsync(_connection, Orders, _person, T0, Work.Request("elsewhere"), "other-work", cancellationToken: Ct);
        var (answered, _) = await Work.OpenAsync(
            _connection, Orders, _person, T0.AddMinutes(2), Work.Request("answered"), cancellationToken: Ct);
        await Work.CloseAsync(_connection, answered, T0.AddMinutes(3), cancellationToken: Ct);

        // Listed: the person's own Request that orders asked back about, with orders' clarification as what it says.
        var (backed, _) = await Work.OpenAsync(
            _connection, _person, Orders, T0.AddMinutes(4), Work.Request("person asks"), cancellationToken: Ct);
        Assert.True(backed.Clarify(Orders, T0.AddMinutes(5)).Succeeded);
        await new MessageRepository(_connection).AppendAsync(
            backed.Id, MessageType.Clarify, Orders, _person, Work.Model,
            new System.Text.Json.Nodes.JsonObject { ["text"] = "which version?" }.ToJsonString(),
            T0.AddMinutes(5), cancellationToken: Ct);
        Assert.True(await repository.SaveAsync(backed, cancellationToken: Ct));

        var (questions, total) = await repository.GetSettleableAsync(Orders, _person, Work.Conversation, 2, Ct);

        Assert.Equal(3, total);
        Assert.Equal([first.Id, second.Id], questions.Select(q => q.ExchangeId));
        Assert.Equal("first", questions[0].Asked);

        var (all, _) = await repository.GetSettleableAsync(Orders, _person, Work.Conversation, 20, Ct);

        Assert.Equal("which version?", all[^1].Asked);
    }
}
