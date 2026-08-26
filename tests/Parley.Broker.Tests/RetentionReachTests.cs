using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// What one agent can make retention do to another agent's traffic.
/// </summary>
/// <remarks>
/// <para>
/// Retention deletes, and the inputs to it are partly agent-chosen: a conversation identifier is
/// a string an agent picks, message timing is an agent's own, and closure is a vote. So the
/// question is not whether the rules are right but whether a peer can steer them — at another
/// pair's text, or away from its own.
/// </para>
/// <para>
/// The three probes here are the ones worth writing, because reading the query answers none of
/// them: they are about what a sequence of legal operations composes to.
/// </para>
/// </remarks>
public sealed class RetentionReachTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Question =
        """{"intent":"where does price live","sub_questions":[{"id":"q1","text":"gross or net?"}],"consequences":"If it does I read it from you; if not I snapshot my own copy and we can diverge."}""";

    private const string Answer =
        """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""";

    private const string Model = "some-model-9";

    private readonly SqliteConnection _connection;

    public RetentionReachTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, null,
                    AgentStatus.Offline),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> MessageCount()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM messages;";

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Runs one settled exchange between two agents inside a named discussion.</summary>
    private async Task Discuss(
        AgentName asker, AgentName answerer, string conversationId, DateTimeOffset at)
    {
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(asker, at, Ct);

        var asked = await broker.AskAsync(
            asker, answerer, Model, Question, at, conversationId, Ct);

        Assert.True(asked.Succeeded, $"expected admission, got {asked.Rejection}");

        await broker.DrainInboxAsync(answerer, at.AddMinutes(1), cancellationToken: Ct);

        Assert.True((await broker.AnswerAsync(
            asked.Exchange!.Id, answerer, Model, Answer, at.AddMinutes(2),
            cancellationToken: Ct)).Succeeded);

        await broker.DrainInboxAsync(asker, at.AddMinutes(3), cancellationToken: Ct);
    }

    [Fact]
    public async Task A_bystander_can_hasten_the_forgetting_of_a_discussion_it_joined()
    {
        // Not a defect in retention, and worth writing down as the thing it is.
        //
        // SPEC.md §5.0 derives conversation membership from naming the identifier, and says so:
        // an identifier is a claim rather than a verified fact, so an agent that knows one — and
        // they are agent-chosen strings like "pricing" — joins by asking anybody a question
        // inside it. §5.0 then polls only participants holding a live exchange, on the reasoning
        // that an agent whose exchanges have all closed has already said what it had to say. A
        // discussion where everybody has finished therefore polls nobody, nobody blocks, and the
        // proposal carries at once.
        //
        // That much is §5.0's design. What §9.4 adds is a consequence: a concluded discussion is
        // prunable without waiting out the idle period, so those two calls also delete two other
        // agents' message text up to the idle period early. The bound is what makes it
        // tolerable — it removes nothing the policy was not going to remove, reaches nothing
        // still in flight, and leaves the exchange rows §15 reads untouched — and the bound is
        // what this pins. If either half of it ever stops holding, this fails.
        await Discuss(Orders, Billing, "pricing", T0);

        // A second discussion between the same pair, so the blast radius is observed rather than
        // assumed: whatever the bystander reaches, it must not be this.
        await Discuss(Orders, Billing, "unrelated", T0.AddMinutes(5));

        var inventory = new Broker(_connection, BrokerOptions.Default);
        await inventory.StartAsync(Inventory, T0.AddMinutes(10), Ct);

        // One: join, by naming the discussion on a question of its own.
        var joined = await inventory.AskAsync(
            Inventory, Orders, Model, Question, T0.AddMinutes(11), "pricing", Ct);

        Assert.True(joined.Succeeded, $"expected admission, got {joined.Rejection}");

        await inventory.DrainInboxAsync(Orders, T0.AddMinutes(12), cancellationToken: Ct);

        Assert.True((await inventory.AnswerAsync(
            joined.Exchange!.Id, Orders, Model, Answer, T0.AddMinutes(13),
            cancellationToken: Ct)).Succeeded);

        await inventory.DrainInboxAsync(Inventory, T0.AddMinutes(14), cancellationToken: Ct);

        Assert.Equal(6, await MessageCount());

        // Two: propose an end. Nobody holds a live exchange, so nobody is polled and nothing
        // blocks — it closes in this call rather than after §5.0's silence window.
        var proposal = await inventory.ConcludeAsync(
            "pricing", Inventory, finished: true, T0.AddMinutes(15), Ct);

        Assert.True(proposal.Concluded);

        // Three days early, on the next sweep.
        var tick = await inventory.TickAsync(T0.AddMinutes(16), Ct);

        Assert.Equal(4, tick.Retention.Removed);

        var messages = new MessageRepository(_connection);

        Assert.Empty(await messages.GetConversationAsync("pricing", Ct));

        // And no further. The other discussion is the same pair and is untouched, because the
        // group retention judges is the conversation and this one is not in it.
        Assert.Equal(2, await MessageCount());
        Assert.Equal(2, (await messages.GetConversationAsync("unrelated", Ct)).Count);

        // Nor is the record §15 reads, which was never the bystander's to touch.
        var exchanges = new ExchangeRepository(_connection);

        Assert.Equal(
            Resolution.Answered,
            (await exchanges.GetAsync(joined.Exchange.Id, cancellationToken: Ct))!.Resolution);
    }

    [Fact]
    public async Task Traffic_in_a_discussion_cannot_pin_it_past_the_age_cap()
    {
        // The other direction: an agent keeping somebody else's text alive rather than deleting
        // it. The idle rule reads the whole group's newest message, so one notification a day
        // into a discussion resets it indefinitely — and Notify is the one message type with no
        // guard of its own until §13's per-sender rate limits exist.
        //
        // The age cap is what makes that bounded, and this is the case it was made absolute for.
        await Discuss(Orders, Billing, "pricing", T0);

        var inventory = new Broker(_connection, BrokerOptions.Default);
        await inventory.StartAsync(Inventory, T0, Ct);

        // A notification a day for forty days, each one making the discussion look busy.
        for (var day = 1; day <= 40; day++)
        {
            var sent = await inventory.NotifyAsync(
                Inventory, Orders, Model,
                $$"""{"text":"still working, day {{day}}"}""",
                T0.AddDays(day), "pricing", Ct);

            Assert.True(sent.Succeeded, $"expected the notification to store, got {sent.Rejection}");
        }

        // Thirty-one days on, the original discussion is past the cap however busy it looks.
        var pruner = new Pruner(_connection);
        await pruner.PruneAsync(RetentionPolicy.Default, T0.AddDays(41), Ct);

        var messages = new MessageRepository(_connection);

        Assert.Empty(await messages.GetConversationAsync("pricing", Ct)
            .ContinueWith(t => t.Result.Where(m => m.CreatedUtc <= T0.AddDays(11)), Ct));
    }

    [Fact]
    public async Task An_agent_that_never_collects_cannot_keep_a_question_for_ever()
    {
        // The floor's one clause with no terminal state, probed rather than argued. An agent
        // that never attaches never collects, so nothing but the age cap ever clears what is
        // waiting for it — and a log whose unbounded corner is the full text of every question
        // is the thing §9.4 exists to prevent.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        Assert.True((await broker.AskAsync(Orders, Billing, Model, Question, T0, null, Ct))
            .Succeeded);

        // Billing never attaches. The sweep orphans and then ghosts the exchange, and the ghost
        // notice waits for an orders that never comes back either.
        await broker.TickAsync(T0.AddHours(6), Ct);
        await broker.TickAsync(T0.AddHours(12), Ct);

        Assert.Equal(2, await MessageCount());

        var pruner = new Pruner(_connection);

        // Inside the cap, both are held: one undelivered question, one undelivered ghost.
        var early = await pruner.PruneAsync(RetentionPolicy.Default, T0.AddDays(29), Ct);

        Assert.Equal(0, early.Removed);
        Assert.Equal(2, early.HeldByFloor);

        // Past it, neither survives.
        var late = await pruner.PruneAsync(RetentionPolicy.Default, T0.AddDays(31), Ct);

        Assert.Equal(2, late.Removed);
        Assert.Equal(0, await MessageCount());
    }
}
