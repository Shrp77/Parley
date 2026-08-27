using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// A referral naming more than one owner, end to end.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §5.3 has always said <c>redirect_to</c> carries a list rather than a single name, and
/// argued for it: a sub-question may turn out to span two boundaries, and an answerer that can
/// name both should. The schema accepted a list from the first migration. The tool could not send
/// one — its parameter was a single string — so the reference implementation could not express a
/// documented feature, and nothing failed anywhere to say so.
/// </para>
/// <para>
/// These run through the real tool rather than writing rows, because the gap was between the tool
/// and the storage and a schema-level test would have passed throughout.
/// </para>
/// </remarks>
public sealed class ReferralListTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_answerer_can_name_two_owners_and_both_are_recorded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);
        await fixture.LaunchAsync("inventory@test", ct);
        await fixture.LaunchAsync("pricing@test", ct);

        var asked = await orders.AskAsync(
            "billing@test",
            "Whether the discount ladder and the stock hold are ours.",
            [new SubQuestion("q1", "Who owns the discount ladder and the reservation?")],
            "If billing owns them I stop modelling both here; if not I keep mine and record who does.",
            cancellationToken: ct);

        var exchange = asked["exchange_id"]!.GetValue<long>();

        await billing.InboxAsync(cancellationToken: ct);

        var answered = await billing.AnswerAsync(
            exchange,
            [new Resolution("q1", "out_of_scope", "Neither is mine; they are split.",
                RedirectTo: ["inventory@test", "pricing@test"])],
            cancellationToken: ct);

        Assert.True(answered["ok"]!.GetValue<bool>(), answered.ToJsonString());

        var connection = fixture.Connection;

        // Both names recorded, against the sub-question that was referred.
        Assert.Equal(
            ["inventory@test", "pricing@test"],
            Read(connection,
                "SELECT redirected_to FROM message_referrals ORDER BY redirected_to;"));

        // And the payload the peer receives carries a list, not a string, so a reader does not
        // have to handle two grammars for one field.
        var payload = Read(connection,
            "SELECT payload_json FROM messages WHERE type = 'answer';").Single();

        Assert.Contains("[\"inventory@test\",\"pricing@test\"]", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_owner_is_still_sent_as_a_list()
    {
        // The shape must not vary with its own length. A bare string for one name and an array
        // for two would make every reader — including the schema's referral trigger — validate
        // two grammars for one field.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);
        await fixture.LaunchAsync("inventory@test", ct);

        var asked = await orders.AskAsync(
            "billing@test",
            "Whether the stock hold is ours.",
            [new SubQuestion("q1", "Who owns the reservation?")],
            "If billing owns it I stop modelling it here; if not I keep mine and record who does.",
            cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        await billing.AnswerAsync(
            asked["exchange_id"]!.GetValue<long>(),
            [new Resolution("q1", "out_of_scope", "Not mine.", RedirectTo: ["inventory@test"])],
            cancellationToken: ct);

        var connection = fixture.Connection;

        var payload = Read(connection,
            "SELECT payload_json FROM messages WHERE type = 'answer';").Single();

        Assert.Contains("[\"inventory@test\"]", payload, StringComparison.Ordinal);
        Assert.Equal(["inventory@test"], Read(connection, "SELECT redirected_to FROM message_referrals;"));
    }

    [Fact]
    public async Task A_bad_name_second_in_the_list_is_refused()
    {
        // The hiding place a first-only check creates. The validation walks every entry for the
        // same reason the schema's trigger does, and this is the case that tells the two apart.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);
        await fixture.LaunchAsync("inventory@test", ct);

        var asked = await orders.AskAsync(
            "billing@test",
            "Whether the stock hold is ours.",
            [new SubQuestion("q1", "Who owns the reservation?")],
            "If billing owns it I stop modelling it here; if not I keep mine and record who does.",
            cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        var refused = await billing.AnswerAsync(
            asked["exchange_id"]!.GetValue<long>(),
            [new Resolution("q1", "out_of_scope", "Split.",
                RedirectTo: ["inventory@test", "Not An Agent"])],
            cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>(), refused.ToJsonString());

        var connection = fixture.Connection;

        Assert.Empty(Read(connection, "SELECT redirected_to FROM message_referrals;"));
    }

    private static List<string> Read(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();
        var values = new List<string>();

        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
