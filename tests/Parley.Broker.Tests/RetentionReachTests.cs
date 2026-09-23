using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// What one participant can make retention do to another's traffic.
/// </summary>
/// <remarks>
/// <para>
/// Retention deletes, and the inputs to it are partly participant-chosen: message timing is a participant's own,
/// and closure is a vote. So the question is not whether the rules are right but whether a participant can steer
/// them — at another pair's text, or away from its own.
/// </para>
/// <para>
/// These are about what a sequence of legal operations composes to, which reading the query answers none of.
/// </para>
/// </remarks>
public sealed class RetentionReachTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;

    public RetentionReachTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Requests.Register(_connection, Orders, Billing, Inventory);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> MessageCount()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM messages;";

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>One settled exchange between two agents in a named conversation.</summary>
    private async Task Discuss(AgentName requester, AgentName receiver, string conversationId, DateTimeOffset at, string text = "gross or net?")
    {
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(requester, at, Ct);

        var sent = await broker.RequestAsync(requester, receiver, Requests.Model, Requests.Request(text), at, conversationId, Ct);
        Assert.True(sent.Succeeded, $"expected admission, got {sent.Rejection}");

        await broker.DrainInboxAsync(receiver, at.AddMinutes(1), cancellationToken: Ct);
        Assert.True((await broker.ResultAsync(sent.Exchange!.Id, receiver, Requests.Model, Requests.Result(), at.AddMinutes(2), Ct)).Succeeded);
        await broker.DrainInboxAsync(requester, at.AddMinutes(3), cancellationToken: Ct);
    }

    [Fact]
    public async Task A_bystander_cannot_join_a_finished_conversation_to_hasten_its_forgetting()
    {
        // In 0.4 an agent that knew a conversation's identifier joined it by naming it, and could then propose
        // closure over a finished conversation and have two other agents' text pruned early. A conversation is
        // assigned now (§5.0): a non-participant naming one it holds no work in is refused.
        await Discuss(Orders, Billing, "pricing", T0);

        var inventory = new Broker(_connection, BrokerOptions.Default);
        await inventory.StartAsync(Inventory, T0.AddMinutes(10), Ct);

        var joined = await inventory.RequestAsync(Inventory, Orders, Requests.Model, Requests.Request("let me in"), T0.AddMinutes(11), "pricing", Ct);

        Assert.Equal(RequestRejection.ConversationNotAssignable, joined.Rejection);
        Assert.Equal(ClosureError.NotAParticipant, (await inventory.ConcludeAsync("pricing", Inventory, true, T0.AddMinutes(12), Ct)).Error);

        await inventory.TickAsync(T0.AddMinutes(16), Ct);

        Assert.Equal(2, await MessageCount());
    }

    [Fact]
    public async Task Participants_agreeing_they_are_finished_releases_only_their_own_conversation()
    {
        // Closing short-circuits the idle period, which is the participants' call to make about their own work —
        // and it reaches no further. Another conversation between the same pair is untouched.
        await Discuss(Orders, Billing, "pricing", T0);
        await Discuss(Orders, Billing, "unrelated", T0.AddMinutes(5), "who owns the ladder?");

        var orders = new Broker(_connection, BrokerOptions.Default);
        await orders.StartAsync(Orders, T0.AddMinutes(10), Ct);

        Assert.True((await orders.ConcludeAsync("pricing", Orders, true, T0.AddMinutes(15), Ct)).Concluded);

        Assert.Equal(2, (await orders.TickAsync(T0.AddMinutes(16), Ct)).Retention.Removed);

        var messages = new MessageRepository(_connection);
        Assert.Empty(await messages.GetConversationAsync("pricing", Ct));
        Assert.Equal(2, (await messages.GetConversationAsync("unrelated", Ct)).Count);
    }

    [Fact]
    public async Task Traffic_in_a_conversation_cannot_pin_it_past_the_age_cap()
    {
        // The other direction: keeping text alive rather than deleting it. The idle rule reads the whole
        // conversation's newest message, so a Request a day resets it indefinitely. The age cap is what bounds that.
        await Discuss(Orders, Billing, "pricing", T0);

        for (var day = 1; day <= 40; day++)
        {
            await Discuss(Orders, Billing, "pricing", T0.AddDays(day), $"still working, day {day}");
        }

        await new Pruner(_connection).PruneAsync(RetentionPolicy.Default, T0.AddDays(41), Ct);

        var remaining = await new MessageRepository(_connection).GetConversationAsync("pricing", Ct);

        Assert.DoesNotContain(remaining, m => m.CreatedUtc <= T0.AddDays(10));
    }

    [Fact]
    public async Task Work_nobody_collects_keeps_its_text_until_the_person_closes_it_and_then_not_for_ever()
    {
        // Open work is held absolutely: nothing ends it on a schedule, so nothing deletes its text on one. What
        // bounds that is the flag putting it in front of the person, who closes it; after that the age cap
        // reaches even the notices nobody collected.
        var broker = new Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        Assert.True((await broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(), T0, "pricing", Ct)).Succeeded);

        var pruner = new Pruner(_connection);

        Assert.Equal(1, (await pruner.PruneAsync(RetentionPolicy.Default, T0.AddDays(400), Ct)).HeldByFloor);

        Assert.True(await new OperatorConsole(_connection).ForceConcludeAsync("pricing", "nobody is working on this", T0.AddDays(400), Ct));
        Assert.Equal(3, await MessageCount());

        // The Request is past the cap on its own, and stays while the closed Results that carry it back wait to be
        // collected (§9.4): the cap is measured on them, so the three go together rather than the Request first.
        var early = await pruner.PruneAsync(RetentionPolicy.Default, T0.AddDays(429), Ct);
        Assert.Equal(0, early.Removed);
        Assert.Equal(3, early.HeldByFloor);

        var late = await pruner.PruneAsync(RetentionPolicy.Default, T0.AddDays(431), Ct);
        Assert.Equal(3, late.Removed);
        Assert.Equal(0, await MessageCount());
    }
}
