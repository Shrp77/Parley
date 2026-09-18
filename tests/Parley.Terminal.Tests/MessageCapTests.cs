using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The message cap, measured on both sides of the wire.
/// </summary>
/// <remarks>
/// <para>
/// The spec's edge case is that a message over the cap is <i>refused before it is sent, with the
/// limit stated, rather than by the store after a round trip</i>. That is two claims, and only the
/// first is testable without a broker: the surface refuses, **and the number it refuses at is the
/// number the store enforces**.
/// </para>
/// <para>
/// The second is where a cap of this shape goes wrong. The client assembles the JSON itself and
/// measures that string; the store's constraint is <c>length(payload_json) &lt;= 8000</c>. Those
/// agree only while the broker writes the document verbatim — a broker that re-serialised or
/// re-framed it could grow the string past a cap the client had just cleared, and the person would
/// get the round-trip refusal the edge case exists to prevent. Probed against a real broker over a
/// real socket with a real store behind it, rather than by reading the two constants and observing
/// that they match.
/// </para>
/// <para>
/// Note the one asymmetry that is safe and deliberate: .NET counts UTF-16 code units and SQLite's
/// <c>length()</c> counts characters, so a message full of astral characters is measured *larger*
/// by the client than by the store. That refuses slightly early, which is the direction a cap
/// should err in.
/// </para>
/// </remarks>
public sealed class MessageCapTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _broker;
    private readonly SeatBook _seats;

    public MessageCapTests()
    {
        _broker = LoopbackOperator.Start(_connection);
        _seats = new SeatBook(_ => _broker.Agents);

        // The recipient has to exist and be routable, or a notify is refused for a reason that
        // has nothing to do with size -- which would make this whole probe pass without ever
        // reaching the cap.
        Execute(
            "INSERT INTO nodes (machine, connected, first_seen_utc) "
            + "VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; "
            + "INSERT INTO agents (name, adapter, tier, status) "
            + "VALUES ('orders@test','test','full','active');");
    }

    public async ValueTask DisposeAsync()
    {
        // The seats before the broker: disposing the book ends the attach streams, and ending
        // them against a broker that has already gone is a different path from the one a test
        // wants to exercise on the way out.
        _seats.Dispose();

        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task A_message_the_surface_accepts_is_one_the_store_accepts()
    {
        // The boundary from below. A payload assembled to sit just under the cap has to survive
        // the whole path -- if the broker grew it on the way in, this is where that shows.
        var token = TestContext.Current.CancellationToken;
        var voice = await SeatedAsync(token);

        var said = await voice.RequestAsync(Recipient, Fill(Room()), Conversation, token);

        // `StartsWith`, because a send now also reports what became of the recipient. What this
        // test is about is the row below: that a payload the surface accepted reached the store.
        Assert.StartsWith("Sent.", said, StringComparison.Ordinal);

        Assert.Equal(1, Rows());
    }

    [Fact]
    public async Task A_message_over_the_cap_never_reaches_the_broker()
    {
        // The boundary from above, and the half the edge case actually names. The refusal has to
        // come from the surface, so the store must still be empty afterwards -- a refusal that
        // arrived as an error from the broker would look the same to the person and be the thing
        // the spec forbids.
        var token = TestContext.Current.CancellationToken;
        var voice = await SeatedAsync(token);

        var refusal = await voice.RequestAsync(Recipient, Fill(Room() + 1), Conversation, token);

        Assert.Contains("the limit is", refusal, StringComparison.Ordinal);

        // And it states the limit, which is the rest of the requirement.
        Assert.Contains(
            MessageLimits.MaxPayloadCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
            refusal,
            StringComparison.Ordinal);

        Assert.Equal(0, Rows());
    }

    /// <summary>How much of the ask fits once the rest of the Request is counted.</summary>
    /// <returns>The number of characters of text that exactly fills the cap.</returns>
    /// <remarks>
    /// Derived rather than hard-coded, and built in the shape the seat sends. A literal here would
    /// silently stop testing the boundary the day the payload grew a field, which is the way a
    /// boundary test quietly becomes a test of the middle of the range. Since 0.7 the framing is
    /// one key, so this is nearly the whole cap — which is the point of the change and not a
    /// reason to stop deriving it.
    /// </remarks>
    private static int Room()
    {
        var framing = new System.Text.Json.Nodes.JsonObject { ["ask"] = string.Empty }.ToJsonString().Length;

        return MessageLimits.MaxPayloadCharacters - framing;
    }

    /// <summary>Text of an exact length, in characters the JSON encoder will not expand.</summary>
    /// <param name="characters">How long.</param>
    /// <returns>The text.</returns>
    /// <remarks>
    /// Plain ASCII letters on purpose: a quote or a backslash would be escaped into two characters
    /// by the serialiser, so the assembled document would be longer than the text and the boundary
    /// would move under the test.
    /// </remarks>
    private static string Fill(int characters) => new('a', characters);

    /// <summary>
    /// The conversation these are said in.
    /// </summary>
    /// <remarks>
    /// Named rather than blank. An empty identifier is refused before the cap is ever measured --
    /// which the first version of this probe discovered by asserting on the wrong refusal, and
    /// which is worth a line here so the next reader does not rediscover it the same way.
    /// </remarks>
    private const string Conversation = "cap-probe";

    /// <summary>Who the message is addressed to.</summary>
    private static AgentName Recipient => AgentName.Create("orders@test");

    /// <summary>Seats a person against the loopback broker and hands back their voice.</summary>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The voice.</returns>
    private async Task<SeatVoice> SeatedAsync(CancellationToken cancellationToken)
    {
        Assert.True(Seat.TryRead("arvind", out var me, out _));

        // Attached, and the book is a field rather than a local: disposing it here would release
        // the attachment before the message was ever sent, and the broker would refuse for want
        // of a sender rather than for size -- which is the probe passing for the wrong reason,
        // in the direction that looks like a failure rather than the one that looks like a pass.
        var taken = await _seats.TakeAsync(
            new ProjectSettings(ProjectName.Create("alpha"), string.Empty, _broker.Address, null, null),
            me,
            [],
            cancellationToken);

        Assert.True(taken.Seated);

        return new SeatVoice(_broker.Agents, me);
    }

    /// <summary>Runs one statement against the store behind the broker.</summary>
    /// <param name="sql">What to run.</param>
    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();

        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>How many messages the store is holding.</summary>
    /// <returns>The count.</returns>
    private int Rows()
    {
        using var count = _connection.CreateCommand();

        count.CommandText = "SELECT count(*) FROM messages";

        return Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
