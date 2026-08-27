using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// What is kept when the size cap refuses a message, and what is deliberately not.
/// </summary>
/// <remarks>
/// <para>
/// The cap of rule 8.5 is a number nobody has ever had evidence for — it was two thousand
/// because that sounded like enough, and it is four thousand for a reason of the same quality.
/// This is what makes the next change to it measured, so the checks here are about whether the
/// record can actually answer the question: how often, how far over at worst, and how far over
/// on average.
/// </para>
/// <para>
/// The other half is what must never be kept. Storing the payload that was refused for being too
/// long would retain exactly the material the cap exists to keep out, place it outside the
/// retention policy that governs messages, and make instrumenting a poisoning control into a
/// poisoning vector.
/// </para>
/// </remarks>
public sealed class SizeRefusalTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly SizeRefusalRepository _refusals;

    public SizeRefusalTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _refusals = new SizeRefusalRepository(_connection);

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<int> Record(AgentName agent, MessageType type, int characters, int at = 0) =>
        _refusals.RecordAsync(
            agent, type, characters, MessageLimits.MaxPayloadCharacters, T0.AddMinutes(at), Ct);

    [Fact]
    public async Task One_refusal_is_recorded_whole()
    {
        await Record(Orders, MessageType.Answer, 4500);

        var only = Assert.Single(await _refusals.GetAllAsync(Ct));

        Assert.Equal(Orders, only.Agent);
        Assert.Equal(MessageType.Answer, only.Type);
        Assert.Equal(1, only.Refusals);
        Assert.Equal(4500, only.Worst);
        Assert.Equal(4500, only.Total);
        Assert.Equal(MessageLimits.MaxPayloadCharacters, only.Cap);
        Assert.Equal(T0, only.LastUtc);
    }

    [Fact]
    public async Task Repeated_refusals_accumulate_onto_one_row()
    {
        // The property that makes this safe to leave on: the table is bounded by the registry,
        // not by how badly an agent behaves. An agent looping on an oversized answer updates a
        // row rather than filling a disk, which is why there is no per-event log here.
        await Record(Orders, MessageType.Answer, 4100, at: 0);
        await Record(Orders, MessageType.Answer, 9000, at: 5);
        await Record(Orders, MessageType.Answer, 4200, at: 9);

        var only = Assert.Single(await _refusals.GetAllAsync(Ct));

        Assert.Equal(3, only.Refusals);
        Assert.Equal(9000, only.Worst);
        Assert.Equal(17300, only.Total);
        Assert.Equal(T0.AddMinutes(9), only.LastUtc);
    }

    [Fact]
    public async Task The_mean_separates_a_cap_set_low_from_somebody_sending_a_file()
    {
        // The distinction the whole record exists to make. Forty refusals just over the cap is
        // an argument for raising it; one refusal at ten times the cap is an argument for
        // nothing at all, and the worst case alone cannot tell them apart.
        await Record(Orders, MessageType.Answer, 4050, at: 0);
        await Record(Orders, MessageType.Answer, 4100, at: 1);
        await Record(Billing, MessageType.Answer, 40000, at: 2);

        var all = await _refusals.GetAllAsync(Ct);
        var tight = all.Single(r => r.Agent == Orders);
        var enormous = all.Single(r => r.Agent == Billing);

        Assert.Equal(4075, tight.Total / tight.Refusals);
        Assert.Equal(40000, enormous.Total / enormous.Refusals);

        // Worst first, so the outlier is the first thing an operator sees.
        Assert.Equal(Billing, all[0].Agent);
    }

    [Fact]
    public async Task Each_message_type_is_counted_apart()
    {
        // An answer that will not fit and a question that will not fit are different problems:
        // one is an answerer with too much to say, the other an asker writing an essay.
        await Record(Orders, MessageType.Answer, 4100);
        await Record(Orders, MessageType.Question, 4200);

        var all = await _refusals.GetAllAsync(Ct);

        Assert.Equal(2, all.Count);
        Assert.All(all, r => Assert.Equal(1, r.Refusals));
    }

    [Fact]
    public async Task Every_type_an_agent_can_send_round_trips()
    {
        // The type is written out as a token so the schema can validate it, which means the
        // formatter and the parser have to agree. One added to a single side compiles cleanly
        // and fails only when a row carrying it is read back.
        MessageType[] sendable =
            [MessageType.Question, MessageType.Clarify, MessageType.Answer, MessageType.Notify];

        foreach (var type in sendable)
        {
            await Record(Orders, type, 5000);
        }

        var all = await _refusals.GetAllAsync(Ct);

        Assert.Equal(sendable.Length, all.Count);
        Assert.Equal(
            sendable.OrderBy(t => t.ToString(), StringComparer.Ordinal),
            all.Select(r => r.Type).OrderBy(t => t.ToString(), StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_ghost_notice_has_no_place_here()
    {
        // Found by writing the loop over every message type and watching it throw. Ghost is the
        // fifth, and it is the broker's own: §14 produces it when an exchange is abandoned, and
        // GhostNotice trims it to fit rather than being refused. A row recording one would be
        // the broker reporting that it refused itself, which is not evidence about whether the
        // cap suits the agents. Refusing it here keeps that out of the record rather than
        // leaving a token the schema would reject on the way in.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Record(Orders, MessageType.Ghost, 5000));

        Assert.Empty(await _refusals.GetAllAsync(Ct));
    }

    [Fact]
    public async Task A_total_past_two_billion_is_still_readable()
    {
        // The read threw an OverflowException before the total was widened, which took the
        // operator's command out permanently for that deployment with no way back short of
        // editing the database. Reachable rather than theoretical: fifty thousand refusals at
        // forty thousand characters, which is one looping agent for an afternoon — and this is a
        // feature whose whole purpose is to still answer a question years later.
        await using var seed = _connection.CreateCommand();
        seed.CommandText =
            "INSERT INTO size_refusals (agent, type, refusals, worst, total, cap, last_utc) " +
            "VALUES ('orders@test', 'answer', 60000, 40000, 2400000000, 4000, $when);";
        seed.Parameters.AddWithValue("$when", ExchangeRow.FormatTimestamp(T0));
        await seed.ExecuteNonQueryAsync(Ct);

        var only = Assert.Single(await _refusals.GetAllAsync(Ct));

        Assert.Equal(2_400_000_000L, only.Total);
        Assert.Equal(40000, only.Total / only.Refusals);
    }

    [Fact]
    public void The_schema_keeps_no_refused_text()
    {
        // The check that matters most, and the one that would be easy to lose to a helpful
        // addition later. There is nowhere to put a payload, so nobody can decide to.
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('size_refusals');";

        using var reader = command.ExecuteReader();
        var columns = new List<string>();

        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Equal(["agent", "type", "refusals", "worst", "total", "cap", "last_utc"], columns);
    }

    [Fact]
    public async Task A_refusal_below_the_cap_is_refused_by_the_schema()
    {
        // A row saying a message was refused for being shorter than the limit is a bug in the
        // caller, and the sort that would otherwise sit in the data for months and then be used
        // to argue the cap down.
        await using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO size_refusals (agent, type, refusals, worst, total, cap, last_utc) " +
            "VALUES ('orders@test', 'answer', 1, 100, 100, 4000, '2026-01-01T00:00:00Z');";

        var failure = await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(Ct));

        Assert.Contains("CHECK constraint failed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retiring_an_agent_does_not_strand_its_record()
    {
        // The row cascades with the registration, like a credential does. Keeping counts for an
        // agent nobody can address would leave the evidence describing a deployment that no
        // longer exists.
        await Record(Orders, MessageType.Answer, 5000);

        await using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM agents WHERE name = 'orders@test';";
        await command.ExecuteNonQueryAsync(Ct);

        Assert.Empty(await _refusals.GetAllAsync(Ct));
    }
}
