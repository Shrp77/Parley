using System.Text.Json.Nodes;
using Dapper;
using Parley.Broker;
using Parley.Core;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What an agent sees and says when the person writes to it (SPEC.md §5.6): its open questions to the person beside
/// the person's request, and <c>settles</c> on <c>parley_result</c> and <c>parley_clarify</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the agent's own tools</b>, because what matters is what reaches a model: a list the broker sends and the
/// adapter drops would pass every broker test and leave the agent searching for its own questions.
/// </para>
/// <para>
/// <b>The person's request is seeded behind the wire</b>, through a broker over the fixture's own store: an agent's
/// adapter may not attach as the person (§12.3), and a surface is not what this suite tests.
/// </para>
/// </remarks>
public sealed class SettlesToolTests
{
    private const string Conversation = "invoice-export";

    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Person = AgentName.Create("human-user@test");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> AsksThePersonAsync(ParleyTools tools, string ask)
    {
        var sent = await tools.RequestAsync(Person.Value, ask, Conversation, cancellationToken: Ct);

        return (long)sent["exchange_id"]!;
    }

    private static async Task<long> PersonWritesAsync(ServerFixture fixture, AgentName to, string ask, int minute)
    {
        var broker = new Broker.Broker(fixture.Connection, BrokerOptions.Default);

        broker.ServeFrom(T0);

        var sent = await broker.RequestAsync(
            Person, to, "human:human-user", new JsonObject { ["ask"] = ask }.ToJsonString(), T0.AddMinutes(minute),
            Conversation, Ct);

        Assert.True(sent.Exchange is not null, $"refused: {sent.Admission.Rejection}");

        return sent.Exchange!.Id;
    }

    [Fact]
    public async Task The_inbox_lists_the_agents_open_questions_beside_the_persons_request_and_says_what_to_do()
    {
        await using var fixture = new ServerFixture(T0);
        var (_, orders) = await fixture.LaunchAsync("orders@test", Ct);

        var first = await AsksThePersonAsync(orders, "Which invoice schema should the export target?");
        var second = await AsksThePersonAsync(orders, "Gross or net?");
        var line = await PersonWritesAsync(fixture, AgentName.Create("orders@test"), "Schema 12.", 10);

        var inbox = await orders.InboxAsync(cancellationToken: Ct);
        var request = inbox["messages"]!.AsArray().Single(m => (long)m!["exchange_id"]! == line)!;
        var questions = request["your_open_questions_to_them"]!.AsArray();

        Assert.Equal([first, second], questions.Select(q => (long)q!["exchange_id"]!));
        Assert.Equal("Which invoice schema should the export target?", (string?)questions[0]!["you_asked"]);
        Assert.Contains("settles", (string?)request["settling"], StringComparison.Ordinal);
        Assert.Contains("has not answered it", (string?)request["settling"], StringComparison.Ordinal);
        Assert.Null(request["more_open_questions"]);
    }

    [Fact]
    public async Task A_result_with_settles_closes_what_it_names_and_says_so()
    {
        await using var fixture = new ServerFixture(T0);
        var (_, orders) = await fixture.LaunchAsync("orders@test", Ct);

        var first = await AsksThePersonAsync(orders, "Which invoice schema should the export target?");
        var second = await AsksThePersonAsync(orders, "Gross or net?");
        var line = await PersonWritesAsync(fixture, AgentName.Create("orders@test"), "Schema 12.", 10);

        _ = await orders.InboxAsync(cancellationToken: Ct);

        var said = await orders.ResultAsync(
            line, "answered", "Targeting schema 12; that settles my question about the schema.", settles: [first],
            cancellationToken: Ct);

        Assert.True((bool)said["ok"]!, said.ToJsonString());
        Assert.Equal([first], said["settled"]!.AsArray().Select(id => (long)id!));

        Assert.Equal("settled", fixture.Connection.ExecuteScalar<string>(
            "SELECT outcome FROM exchanges WHERE id = @X;", new { X = first }));
        Assert.Null(fixture.Connection.ExecuteScalar<string?>(
            "SELECT outcome FROM exchanges WHERE id = @X;", new { X = second }));
    }

    [Fact]
    public async Task A_bad_name_comes_back_as_a_sentence_naming_it_and_nothing_is_sent()
    {
        await using var fixture = new ServerFixture(T0);
        var (_, orders) = await fixture.LaunchAsync("orders@test", Ct);

        var line = await PersonWritesAsync(fixture, AgentName.Create("orders@test"), "Schema 12.", 10);

        _ = await orders.InboxAsync(cancellationToken: Ct);

        var said = await orders.ResultAsync(line, "answered", "Noted.", settles: [line], cancellationToken: Ct);

        Assert.False((bool)said["ok"]!);
        Assert.Contains("Nothing was sent", (string?)said["why"], StringComparison.Ordinal);

        var refused = Assert.Single(said["cannot_settle"]!.AsArray())!;

        Assert.Equal(line, (long)refused["exchange_id"]!);
        Assert.Contains("does not owe the next move", (string?)refused["why"], StringComparison.Ordinal);

        Assert.Equal(0L, fixture.Connection.ExecuteScalar<long>(
            "SELECT count(*) FROM messages WHERE exchange_id = @X AND type = 'result';", new { X = line }));
    }

    [Fact]
    public async Task A_request_from_another_agent_carries_no_list()
    {
        await using var fixture = new ServerFixture(T0);
        var (_, orders) = await fixture.LaunchAsync("orders@test", Ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", Ct);

        await AsksThePersonAsync(orders, "Which invoice schema?");
        var fromBilling = (long)(await billing.RequestAsync("orders@test", "Gross or net?", cancellationToken: Ct))["exchange_id"]!;

        var inbox = await orders.InboxAsync(cancellationToken: Ct);
        var request = inbox["messages"]!.AsArray().Single(m => (long)m!["exchange_id"]! == fromBilling)!;

        Assert.Null(request["your_open_questions_to_them"]);
    }
}
