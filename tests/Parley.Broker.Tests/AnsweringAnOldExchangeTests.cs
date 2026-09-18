using Dapper;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// An exchange opened under 0.5 or 0.6 is still answerable after the upgrade, by its own item identifiers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing times out (§14)</b>, so this is not a narrow window. An exchange the owner's deployment opened last
/// week is open now, is open after migration 011 runs, and stays open until somebody answers it — which means 0.7
/// has to keep accepting a Result shaped for a Request 0.7 can no longer send.
/// </para>
/// <para>
/// The trigger 011 replaced governs writing a Request; <c>tr_result_is_well_formed</c> was deliberately left
/// alone. So what is checked here is the broker's own matching of outcomes against the <em>stored</em> Request —
/// the assumption a simplification would quietly introduce is that a Request has one item called <c>request</c>,
/// and nothing else in the suite would catch it, because every other test writes what this build writes.
/// </para>
/// </remarks>
public sealed class AnsweringAnOldExchangeTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = "claude-opus-5";

    private const string OldRequest =
        """{"intent":"deciding where tax is added","items":[{"id":"r1","kind":"work","text":"use the order's price"},{"id":"r2","kind":"question","text":"which invoice version?"}],"done_when":"invoices show the order's price"}""";

    private static string Answering(params string[] ids) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["outcomes"] = new System.Text.Json.Nodes.JsonArray([.. ids.Select(id =>
                (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = id,
                    ["outcome"] = "done",
                    ["text"] = "handled",
                })]),
        }.ToJsonString();

    /// <summary>Opens an exchange and leaves a 0.6 Request stored in it.</summary>
    /// <param name="connection">The store.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>The broker and the exchange.</returns>
    /// <remarks>
    /// The Request is sent in this build's shape and then replaced, with the trigger stood down for the one write.
    /// That stands in for a message written before migration 011 ran, which is the only way one can exist: a
    /// stored payload is never rewritten in service, which is the property 011 rests on, so there is no other
    /// route to the state a real upgraded store is in.
    /// </remarks>
    private static async Task<(Broker Broker, long ExchangeId)> WithAnOldExchangeAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var agents = new AgentRepository(connection);

        foreach (var agent in new[] { Orders, Billing })
        {
            await agents.UpsertAsync(
                new AgentRecord(agent, "test", AgentTier.Full, null, null, null, false, AgentStatus.Active),
                cancellationToken);
        }

        var broker = new Broker(connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, cancellationToken);

        var sent = await broker.RequestAsync(
            Orders, Billing, Model, """{"ask":"a placeholder this test replaces"}""", T0, "history",
            cancellationToken);

        Assert.NotNull(sent.Exchange);

        connection.Execute(
            """
            DROP TRIGGER tr_request_is_well_formed;
            UPDATE messages SET payload_json = @payload WHERE type = 'request';
            CREATE TRIGGER tr_request_is_well_formed BEFORE INSERT ON messages
            WHEN NEW.type = 'request' AND json_valid(NEW.payload_json)
            BEGIN
              SELECT RAISE(ABORT, 'a request carries an ask')
               WHERE json_type(NEW.payload_json, '$.ask') IS NOT 'text'
                  OR length(trim(json_extract(NEW.payload_json, '$.ask'))) = 0;
            END;
            """,
            new { payload = OldRequest });

        await broker.DrainInboxAsync(Billing, T0.AddMinutes(1), cancellationToken: cancellationToken);

        return (broker, sent.Exchange!.Id);
    }

    [Fact]
    public async Task It_is_answered_by_the_identifiers_its_own_request_declared()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connection = ParleyDatabase.OpenInMemory();

        var (broker, exchangeId) = await WithAnOldExchangeAsync(connection, ct);

        // The 0.7 spelling is refused, and the refusal names what the stored Request actually asked — which is what
        // makes this recoverable in one move rather than a dead end for an exchange nothing will time out.
        var refused = await broker.ResultAsync(exchangeId, Billing, Model, Answering("request"), T0.AddMinutes(2), ct);

        Assert.False(refused.Succeeded);
        Assert.Equal(["r1", "r2"], refused.ExpectedItems);

        var answered = await broker.ResultAsync(
            exchangeId, Billing, Model, Answering("r1", "r2"), T0.AddMinutes(3), ct);

        Assert.True(answered.Succeeded, answered.Error.ToString());

        Assert.Equal(
            "r1|r2",
            connection.ExecuteScalar<string>(
                "SELECT group_concat(item, '|') FROM (SELECT item FROM result_items ORDER BY item);"));
    }

    [Fact]
    public async Task Answering_only_some_of_its_items_leaves_it_open_and_answerable()
    {
        // The guard that must not have been loosened on the way past. A Result naming one of two would close the
        // exchange with the other item unresolved, and §15 would count work done that had not been.
        var ct = TestContext.Current.CancellationToken;
        using var connection = ParleyDatabase.OpenInMemory();

        var (broker, exchangeId) = await WithAnOldExchangeAsync(connection, ct);

        Assert.False((await broker.ResultAsync(
            exchangeId, Billing, Model, Answering("r1"), T0.AddMinutes(2), ct)).Succeeded);

        Assert.True((await broker.ResultAsync(
            exchangeId, Billing, Model, Answering("r1", "r2"), T0.AddMinutes(3), ct)).Succeeded);
    }

    [Fact]
    public async Task A_new_request_in_the_same_conversation_is_answered_the_new_way()
    {
        // Both shapes live in one conversation after an upgrade, and each is answered on its own terms. A broker
        // that decided from the conversation rather than from the message would get the second one wrong.
        var ct = TestContext.Current.CancellationToken;
        using var connection = ParleyDatabase.OpenInMemory();

        var (broker, old) = await WithAnOldExchangeAsync(connection, ct);

        var fresh = await broker.RequestAsync(
            Orders, Billing, Model, """{"ask":"and which version ships it?"}""", T0.AddMinutes(2), "history", ct);

        Assert.NotNull(fresh.Exchange);

        await broker.DrainInboxAsync(Billing, T0.AddMinutes(3), cancellationToken: ct);

        Assert.True((await broker.ResultAsync(
            old, Billing, Model, Answering("r1", "r2"), T0.AddMinutes(4), ct)).Succeeded);

        Assert.True((await broker.ResultAsync(
            fresh.Exchange!.Id, Billing, Model, Answering("request"), T0.AddMinutes(5), ct)).Succeeded);
    }
}
