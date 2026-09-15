using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That a question 0.4 wrote with an id too long for a Result can still be finished after the upgrade.
/// </summary>
/// <remarks>
/// <para>
/// Found by the review gate, lens 1 run 1.15. 0.4 held sub-question ids to being distinct and nothing else, migration
/// 008 carries an open question forward as an open Request, and <c>result_items</c> holds an item's id to 64
/// characters. Probed against a store built from the embedded migrations: a Result echoing a 65-character id was
/// refused by storage, one leaving it out was refused for not matching, and the exchange stayed open for ever.
/// </para>
/// <para>
/// Through the broker rather than the repositories, because the refusal that closed the door was the broker's check
/// against the Request's items.
/// </para>
/// </remarks>
public sealed class MigratedItemIdTests
{
    private const string Stamp = "2026-01-01T00:00:00.0000000Z";
    private const string Model = "claude-opus-5";

    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset Later = DateTimeOffset.Parse("2026-02-01T00:00:00Z", CultureInfo.InvariantCulture);

    private static readonly string TooLong = "q" + new string('x', MessageLimits.LongestItemId);

    [Fact]
    public async Task A_question_whose_only_id_is_too_long_is_answered_as_a_whole()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connection = await UpgradedAsync([TooLong], ct);
        var broker = new Broker(connection, BrokerOptions.Default);
        await broker.StartAsync(Billing, Later, ct);

        // An agent's natural first reply is the question's own id. Refused, and the refusal says what to send instead
        // (lens 3 run 3.10: it used to reach storage and come back as a constraint's text, naming nothing).
        var echoed = await broker.ResultAsync(
            1, Billing, Model,
            $$"""{"outcomes":[{"id":"{{TooLong}}","outcome":"answered","text":"net"}]}""",
            Later, ct);

        Assert.Equal(TransitionError.OutcomesDoNotMatchItems, echoed.Error);
        Assert.Equal([PayloadShape.WholeRequest], echoed.ExpectedItems);

        var result = await broker.ResultAsync(
            1, Billing, Model,
            $$"""{"outcomes":[{"id":"{{PayloadShape.WholeRequest}}","outcome":"answered","text":"net"}]}""",
            Later.AddMinutes(1), ct);

        Assert.True(result.Succeeded, $"refused: {result.Error}");
        Assert.Equal("CLOSED", connection.ExecuteScalar<string>("SELECT state FROM exchanges WHERE id = 1;"));
    }

    [Fact]
    public async Task Of_a_question_with_one_id_too_long_the_others_are_what_the_result_answers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connection = await UpgradedAsync(["q1", TooLong], ct);
        var broker = new Broker(connection, BrokerOptions.Default);
        await broker.StartAsync(Billing, Later, ct);

        // Echoing both is refused, and the refusal names what it expects: an agent told this can finish.
        var echoed = await broker.ResultAsync(
            1, Billing, Model,
            $$"""{"outcomes":[{"id":"q1","outcome":"answered","text":"net"},{"id":"{{TooLong}}","outcome":"answered","text":"yes"}]}""",
            Later, ct);

        Assert.False(echoed.Succeeded);
        Assert.Equal(["q1"], echoed.ExpectedItems);

        var result = await broker.ResultAsync(
            1, Billing, Model, """{"outcomes":[{"id":"q1","outcome":"answered","text":"net"}]}""", Later.AddMinutes(1), ct);

        Assert.True(result.Succeeded, $"refused: {result.Error}");
        Assert.Equal("CLOSED", connection.ExecuteScalar<string>("SELECT state FROM exchanges WHERE id = 1;"));
    }

    [Fact]
    public async Task A_question_whose_ids_fit_is_still_held_to_them()
    {
        // The control: nothing about the upgrade loosens the rule for a Request whose ids a Result can carry.
        var ct = TestContext.Current.CancellationToken;
        using var connection = await UpgradedAsync(["q1"], ct);
        var broker = new Broker(connection, BrokerOptions.Default);
        await broker.StartAsync(Billing, Later, ct);

        var wrong = await broker.ResultAsync(
            1, Billing, Model,
            $$"""{"outcomes":[{"id":"{{PayloadShape.WholeRequest}}","outcome":"answered","text":"net"}]}""",
            Later, ct);

        Assert.Equal(TransitionError.OutcomesDoNotMatchItems, wrong.Error);
    }

    [Fact]
    public async Task Work_its_receiver_had_collected_under_0_4_is_handed_back_when_it_returns()
    {
        // Lens 1 run 1.20. 0.4 stamped the question delivered when billing collected it, and 008 copies that stamp. 0.5
        // hands collected work back to an agent that returns, reading only the exchange's state and owner and the
        // messages' delivery: all carried. Checked on a store the migration actually built, not reasoned from columns.
        var ct = TestContext.Current.CancellationToken;
        using var connection = await UpgradedAsync(["q1"], ct);
        var broker = new Broker(connection, BrokerOptions.Default);

        var started = await broker.StartAsync(Billing, Later, ct);

        Assert.Equal(1, started.ExchangesRedelivered);

        var collected = await broker.DrainInboxAsync(Billing, Later.AddMinutes(1), cancellationToken: ct);
        var request = Assert.Single(collected.Messages).Message;

        Assert.Equal(MessageType.Request, request.Type);
        Assert.Equal(1, request.ExchangeId);

        // Lens 2 run 2.15: handed back to the agent that owes the work and to nobody else. Orders asked the question, so
        // its return gives it nothing, and the question's words are not delivered to it as though they were addressed to it.
        var requester = new Broker(connection, BrokerOptions.Default);

        Assert.Equal(0, (await requester.StartAsync(Orders, Later.AddMinutes(2), ct)).ExchangesRedelivered);
        Assert.Empty((await requester.DrainInboxAsync(Orders, Later.AddMinutes(3), cancellationToken: ct)).Messages);
    }

    private static async Task<SqliteConnection> UpgradedAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        SchemaMigrator.Configure(connection);

        const string Prefix = "Parley.Store.Migrations.";
        var assembly = typeof(SchemaMigrator).Assembly;

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            if (int.Parse(name.AsSpan(Prefix.Length, 3), CultureInfo.InvariantCulture) > 7)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = await reader.ReadToEndAsync(ct);
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        connection.Execute("PRAGMA user_version = 7;");

        var agents = new AgentRepository(connection);

        foreach (var agent in new[] { Orders, Billing })
        {
            await agents.UpsertAsync(
                new AgentRecord(agent, "test", AgentTier.Full, null, null, null, false, AgentStatus.Active), ct);
        }

        connection.Execute($"INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', '{Stamp}');");
        connection.Execute(
            $"INSERT INTO conversation_participants (conversation_id, agent, joined_utc) VALUES ('pricing','orders@test','{Stamp}'), ('pricing','billing@test','{Stamp}');");

        connection.Execute(
            """
            INSERT INTO exchanges
                (id, pair_key, asker, answerer, owed_by, state, delivery_window_ms, working_window_ms,
                 grace_period_ms, question_hash, opened_utc, expires_utc, closed_utc, resolution, conversation_id)
            VALUES
                (1, 'billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test', 'billing@test',
                 'OPEN', 900000, 14400000, 10800000, 'h1', @Opened, @Opened, NULL, NULL, 'pricing');
            """,
            new { Opened = Stamp });

        var subQuestions = string.Join(",", ids.Select(id => $$"""{"id":"{{id}}","text":"gross or net?"}"""));

        connection.Execute(
            """
            INSERT INTO messages
                (exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash,
                 conversation_id, created_utc, delivered_utc)
            VALUES (1, 'question', 'orders@test', 'billing@test', 'claude-opus-5', @Payload, @Hash, 'pricing', @At, @At);
            """,
            new
            {
                Payload = $$"""{"intent":"where tax is added","sub_questions":[{{subQuestions}}],"consequences":"If gross I stop adding tax at invoice time."}""",
                Hash = new string('a', 64),
                At = Stamp
            });

        // Every migration after schema 7, which is what this store was built as: 008 and whatever came after it.
        Assert.Equal(SchemaMigrator.LatestVersion - 7, SchemaMigrator.Migrate(connection));

        return connection;
    }
}
