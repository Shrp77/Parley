using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Sending into something that stopped being open while it was on screen.
/// </summary>
/// <remarks>
/// <para>
/// The spec's edge case: <i>a conversation is concluded while it is open on screen — sending into
/// it is refused with the reason, not with a generic failure.</i> The distinction is the whole
/// requirement. A terminal that said "the broker refused it and did not say why" would be telling
/// somebody to retry something that will never succeed, and the state that caused it is one they
/// cannot see from where they are standing.
/// </para>
/// <para>
/// Probed against a real broker with a real store, because the refusal being *specific* depends on
/// the wire carrying a reason code and on the surface mapping it — two things a stub would let a
/// test assume.
/// </para>
/// </remarks>
[Integration]
public sealed class ConcludedUnderfootTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _broker;
    private readonly SeatBook _seats;

    public ConcludedUnderfootTests()
    {
        _broker = LoopbackOperator.Start(_connection);
        _seats = new SeatBook(_ => _broker.Agents);

        Execute(
            "INSERT INTO nodes (machine, connected, first_seen_utc) "
            + "VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; "
            + "INSERT INTO agents (name, adapter, tier, status) "
            + "VALUES ('orders@test','test','full','active');");
    }

    public async ValueTask DisposeAsync()
    {
        _seats.Dispose();

        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task Sending_into_a_concluded_conversation_is_refused_with_the_reason()
    {
        var token = TestContext.Current.CancellationToken;
        var voice = await SeatedAsync(token);

        // Open it, the ordinary way. `StartsWith` rather than `Equal`, because a send now also
        // reports what became of the recipient and this test is about the refusal below it.
        Assert.StartsWith(
            "Sent.",
            await voice.RequestAsync(Recipient, Ask, Conversation, token),
            StringComparison.Ordinal);

        // And close it underneath the person, which is what an operator or the other participant
        // does without asking the screen.
        //
        // The timestamp is written in strict round-trip form, seven fractional digits and all.
        // `ExchangeRow.ParseTimestamp` is a `ParseExact` on "O", so a shorter one throws inside
        // the broker and arrives here as "Exception was thrown by handler" -- which is what the
        // first version of this probe spent a cycle mistaking for the defect it was hunting.
        Execute(
            "UPDATE conversations SET state = 'CLOSED', closed_by = 'person', "
            + "closed_utc = '2026-09-02T00:00:00.0000000+00:00' "
            + $"WHERE id = '{Conversation}';");

        var refused = await voice.RequestAsync(Recipient, "And now?", Conversation, token);

        // Named, not generic. "The broker refused it and did not say why" is the fallback this
        // must not fall through to -- it would send somebody off to retry something that cannot
        // succeed, over a state they cannot see from here.
        Assert.Contains("closed", refused, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("did not say why", refused, StringComparison.Ordinal);

        // And nothing was written: a refusal that still stored the message would be worse than a
        // generic error, because the transcript would disagree with what the person was told.
        Assert.Equal(1, Rows());
    }

    [Fact]
    public async Task A_recipient_that_cannot_be_reached_is_named_rather_than_shrugged_at()
    {
        // The neighbouring refusal, and the one most likely to be collapsed into the generic
        // fallback by accident, since both arrive as "not ok" on the same reply.
        var token = TestContext.Current.CancellationToken;
        var voice = await SeatedAsync(token);

        var refused = await voice.RequestAsync(AgentName.Create("nobody@test"), Ask, Conversation, token);

        Assert.DoesNotContain("did not say why", refused, StringComparison.Ordinal);
        Assert.Equal(0, Rows());
    }

    /// <summary>The conversation these are said in.</summary>
    private const string Conversation = "concluded-probe";

    /// <summary>The ask itself, so the refusals below are the only thing these probes can be failing on.</summary>
    private const string Ask = "Is this conversation still open?";

    /// <summary>Who the message is addressed to.</summary>
    private static AgentName Recipient => AgentName.Create("orders@test");

    /// <summary>Seats a person against the loopback broker and hands back their voice.</summary>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The voice.</returns>
    private async Task<SeatVoice> SeatedAsync(CancellationToken cancellationToken)
    {
        // The person seat the broker provides, read from its registry as a surface reads it. The book is a
        // field rather than a local: disposing it here would drop the attachment before anything was sent.
        var registry = await new BrokerClient(_broker.Operators).RegistryAsync(cancellationToken);

        var taken = await _seats.TakeAsync(
            new ProjectSettings(ProjectName.Create("alpha"), string.Empty, _broker.Address, null),
            registry,
            cancellationToken);

        Assert.True(taken.Seated, taken.Problem);

        return new SeatVoice(_broker.Agents, taken.Hold!.Seat);
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

        return Convert.ToInt32(
            count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
