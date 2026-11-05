using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>That a person reading a conversation is told when an agent's next message went to another one (SPEC.md §5.0).</summary>
[Integration]
public sealed class WroteElsewhereTests : IDisposable
{
    private const string Here = "in-flight";
    private const string Older = "sync-around";

    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Person = AgentName.Create("human-user@test");
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;
    private readonly OperatorConsole _console;

    public WroteElsewhereTests()
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

    private async Task<long> AskAsync(AgentName from, AgentName to, string ask, string conversation)
    {
        await _broker.StartAsync(from, T0, Ct);

        return (await _broker.RequestAsync(from, to, "model-a", $$"""{"ask":"{{ask}}"}""", T0, conversation, Ct)).Exchange!.Id;
    }

    private async Task ClarifyAsync(long exchange, AgentName from, string words) =>
        _ = await _broker.ClarifyAsync(exchange, from, "model-a", $$"""{"text":"{{words}}"}""", T0.AddMinutes(1), Ct);

    private async Task AnswerAsync(long exchange, AgentName from) =>
        _ = await _broker.ResultAsync(
            exchange, from, "model-a", $$"""{"outcomes":[{"id":"{{PayloadShape.WholeRequest}}","outcome":"done","text":"done"}]}""", T0.AddMinutes(1), Ct);

    private long LastMessage() => _connection.ExecuteScalar<long>("SELECT MAX(id) FROM messages;");

    private async Task<IReadOnlyList<WroteElsewhere>> NotedHereAsync() =>
        (await _console.ExportAsync(Here, Ct)).WroteElsewhere!;

    [Fact]
    public async Task A_clarify_on_an_older_exchange_straight_after_answering_here_is_noted_here()
    {
        // The case that showed the need: a new conversation with the person, and an exchange left open in an older one.
        var older = await AskAsync(Billing, Orders, "open your pull requests", Older);
        var here = await AskAsync(Person, Orders, "that command failed", Here);

        await AnswerAsync(here, Orders);

        var answered = LastMessage();

        await ClarifyAsync(older, Orders, "your script has the same bad path");

        var noted = Assert.Single(await NotedHereAsync());

        Assert.Equal(
            new WroteElsewhere(LastMessage(), answered, Orders, Billing, Older),
            noted);
    }

    [Fact]
    public async Task It_stays_where_it_was_written_and_the_older_conversation_says_nothing_of_it()
    {
        var older = await AskAsync(Billing, Orders, "open your pull requests", Older);
        var here = await AskAsync(Person, Orders, "that command failed", Here);

        await AnswerAsync(here, Orders);
        await ClarifyAsync(older, Orders, "your script has the same bad path");

        var said = await _console.ExportAsync(Older, Ct);

        Assert.Equal(MessageType.Clarify, said.Messages[^1].Type);
        Assert.Empty(said.WroteElsewhere!);
    }

    [Fact]
    public async Task The_reply_and_what_follows_it_are_the_other_conversation_going_on_and_are_not_noted()
    {
        var older = await AskAsync(Billing, Orders, "open your pull requests", Older);
        var here = await AskAsync(Person, Orders, "that command failed", Here);

        await AnswerAsync(here, Orders);
        await ClarifyAsync(older, Orders, "your script has the same bad path");
        await ClarifyAsync(older, Billing, "fixed");
        await ClarifyAsync(older, Orders, "thanks, those names match");

        Assert.Single(await NotedHereAsync());
    }

    [Fact]
    public async Task An_agent_that_heard_something_elsewhere_first_is_answering_that_and_is_not_noted()
    {
        var here = await AskAsync(Person, Orders, "that command failed", Here);

        await AnswerAsync(here, Orders);

        var older = await AskAsync(Billing, Orders, "open your pull requests", Older);

        await ClarifyAsync(older, Orders, "which ones?");

        Assert.Empty(await NotedHereAsync());
    }

    [Fact]
    public async Task A_result_elsewhere_closes_that_exchange_and_is_not_noted()
    {
        var older = await AskAsync(Billing, Orders, "open your pull requests", Older);
        var here = await AskAsync(Person, Orders, "that command failed", Here);

        await AnswerAsync(here, Orders);
        await AnswerAsync(older, Orders);

        Assert.Empty(await NotedHereAsync());
    }

    [Fact]
    public async Task A_request_elsewhere_is_noted_like_a_clarify()
    {
        var here = await AskAsync(Person, Orders, "that command failed", Here);

        await AnswerAsync(here, Orders);
        _ = await AskAsync(Orders, Billing, "is the script yours?", Older);

        var noted = Assert.Single(await NotedHereAsync());

        Assert.Equal((Orders, Billing, Older), (noted.From, noted.To, noted.Conversation));
    }

    [Fact]
    public async Task Writing_to_the_person_elsewhere_is_not_an_agent_going_elsewhere()
    {
        var here = await AskAsync(Person, Orders, "that command failed", Here);

        await AnswerAsync(here, Orders);
        _ = await AskAsync(Orders, Person, "one for you", Older);

        Assert.Empty(await NotedHereAsync());
    }

    [Fact]
    public async Task The_person_going_elsewhere_is_not_noted()
    {
        var here = await AskAsync(Person, Orders, "that command failed", Here);

        await AnswerAsync(here, Orders);
        _ = await AskAsync(Person, Billing, "another thing", Older);

        Assert.Empty(await NotedHereAsync());
    }

    [Fact]
    public async Task What_happened_before_this_conversation_began_is_not_read()
    {
        var older = await AskAsync(Billing, Orders, "open your pull requests", Older);

        await ClarifyAsync(older, Orders, "which ones?");
        await ClarifyAsync(older, Billing, "all of them");

        _ = await AskAsync(Person, Orders, "that command failed", Here);

        Assert.Empty(await NotedHereAsync());
    }

    [Fact]
    public async Task Writing_on_in_this_conversation_is_not_going_elsewhere()
    {
        var here = await AskAsync(Person, Orders, "that command failed", Here);

        await ClarifyAsync(here, Orders, "which command?");
        _ = await _broker.RequestAsync(Orders, Billing, "model-a", """{"ask":"is the script yours?"}""", T0.AddMinutes(2), null, Ct);

        Assert.Empty(await NotedHereAsync());
    }

    [Fact]
    public async Task A_conversation_nothing_left_has_no_notes()
    {
        var here = await AskAsync(Person, Orders, "that command failed", Here);

        await AnswerAsync(here, Orders);

        Assert.Empty(await NotedHereAsync());
    }
}
