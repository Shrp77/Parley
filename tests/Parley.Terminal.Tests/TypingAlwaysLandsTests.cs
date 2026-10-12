using Microsoft.Data.Sqlite;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a message somebody typed reaches the agents, whatever stands between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner sent one message to two agents and it reached neither</b>, with two different sentences about
/// turns: <i>"the other side holds the next move on that exchange"</i> and <i>"you sent that request, so its
/// receiver sends the Result"</i>. Both refusals were correct. What was wrong is that the surface chose those
/// acts, and that a refusal took somebody's words with it.
/// </para>
/// <para>
/// <b>Against a real broker, because the refusals were the broker's and correct.</b> These drive it through
/// each state a pair can be in. They used to assert which act the surface derived for each; the surface no longer makes the
/// derivation, since every line is now a Request (SPEC.md §5.6), so what they hold is the pair state the room still
/// reads — whose move it is, and who opened it — and that a new Request is admitted whatever is open.
/// </para>
/// </remarks>
public sealed class TypingAlwaysLandsTests : IDisposable
{
    private static readonly AgentName Me = AgentName.Create("arvind@test");
    private static readonly AgentName Peer = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Conversation = "room";

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Where_the_peer_asked_this_person_holds_the_move()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await OpenAsync(ct);

        await broker.RequestAsync(Peer, Me, "model", Ask("is the build green?"), T0, Conversation, ct);

        var pair = await PairAsync(broker, ct);

        Assert.Equal(Turn.Mine, pair.Turn);
        Assert.False(pair.Asked, "the peer opened it, so this person is the receiver");
    }

    [Fact]
    public async Task Where_this_person_asked_and_the_peer_clarified_this_person_holds_the_move()
    {
        // **The second refusal the owner met.** Whose move it is says this person's; what they owe is the
        // answer to a clarification, and §5.3 gives the Result to the receiver alone — so a Result here is
        // refused with "you sent that request, so its receiver sends the Result".
        var ct = TestContext.Current.CancellationToken;
        var broker = await OpenAsync(ct);

        var sent = await broker.RequestAsync(Me, Peer, "model", Ask("what is the licence key?"), T0, Conversation, ct);

        Assert.NotNull(sent.Exchange);

        await broker.ClarifyAsync(sent.Exchange!.Id, Peer, "model", Say("which environment?"), T0.AddMinutes(1), ct);

        var pair = await PairAsync(broker, ct);

        Assert.Equal(Turn.Mine, pair.Turn);
        Assert.True(pair.Asked, "this person opened it, so they are the requester");
    }

    [Fact]
    public async Task Where_this_person_asked_the_peer_holds_the_move()
    {
        // **The first refusal the owner met.** There is nothing to add to an exchange whose next move belongs
        // to the other side, so typing must not try: it opens one of its own.
        var ct = TestContext.Current.CancellationToken;
        var broker = await OpenAsync(ct);

        await broker.RequestAsync(Me, Peer, "model", Ask("what is the licence key?"), T0, Conversation, ct);

        var pair = await PairAsync(broker, ct);

        Assert.Equal(Turn.Theirs, pair.Turn);
    }

    [Fact]
    public async Task And_a_new_request_is_admitted_while_that_one_is_still_open()
    {
        // The whole of the promise: the words go. §7.1's cap is off by default, so a second exchange between
        // the same pair is ordinary.
        var ct = TestContext.Current.CancellationToken;
        var broker = await OpenAsync(ct);

        await broker.RequestAsync(Me, Peer, "model", Ask("what is the licence key?"), T0, Conversation, ct);

        var again = await broker.RequestAsync(
            Me, Peer, "model", Ask("and where does it live?"), T0.AddMinutes(1), Conversation, ct);

        Assert.True(
            again.Exchange is not null,
            $"the second request was refused: {again.Admission.Rejection}");
    }

    private static string Ask(string said) => $$"""{"ask":"{{said}}"}""";

    private static string Say(string said) => $$"""{"text":"{{said}}"}""";

    private async Task<Parley.Broker.Broker> OpenAsync(CancellationToken cancellationToken)
    {
        var agents = new AgentRepository(_connection);

        foreach (var agent in new[] { Me, Peer })
        {
            await agents.UpsertAsync(
                new AgentRecord(agent, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                cancellationToken);
        }

        var broker = new Parley.Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Me, T0, cancellationToken);
        await broker.StartAsync(Peer, T0, cancellationToken);

        return broker;
    }

    /// <summary>The pair state this surface would derive, from the two reads it actually makes.</summary>
    private async Task<PairState> PairAsync(Parley.Broker.Broker broker, CancellationToken cancellationToken)
    {
        var console = new Parley.Broker.OperatorConsole(_connection);
        var export = await console.ExportAsync(Conversation, cancellationToken);
        var waiting = await console.DescribeWaitingAsync(cancellationToken);

        Assert.NotNull(export);

        _ = broker;

        return Assert.Single(Pairing.From(export!, waiting, Me));
    }
}
