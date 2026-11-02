using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the terminal and the console never disagree about a conversation.
/// </summary>
/// <remarks>
/// <para>
/// SC-003, and the test that justifies extracting <c>Parley.Client</c> at all. Two surfaces
/// deriving participants or visibility separately is how they come to contradict each other, and
/// this repository has shipped that twice in a week: a frame disagreeing with the page beneath it
/// about a waiting count, and again about a seat. A reader who checks one against the other and
/// finds them different has no way to tell which is lying.
/// </para>
/// <para>
/// <b>Over a real socket, against a real broker.</b> The conversation is built by asking and
/// answering through the broker rather than by writing rows, so the export both surfaces read is
/// the one a deployment would produce — including the exchange states, which travel with the
/// messages precisely so a client cannot hold one without the other.
/// </para>
/// </remarks>
[Integration]
public sealed class AgreementTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public AgreementTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task Both_surfaces_name_the_same_participants()
    {
        var export = await ThreeWayConversationAsync();

        // The console's transcript and the terminal's both start from Visibility.Participants,
        // which is the point: there is one derivation, so there is one answer. This asserts that
        // the answer actually reaches both renderings rather than only the shared function.
        var console = Transcript.Write("threeway", "alpha", export.Messages, export.Exchanges);
        var terminal = Terminal(export);

        foreach (var participant in new[] { Orders, Billing, Inventory })
        {
            Assert.Contains(participant.Value, console, StringComparison.Ordinal);
            Assert.Contains(participant.Value, terminal, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Both_surfaces_agree_about_who_could_not_see_what()
    {
        var export = await ThreeWayConversationAsync();

        var console = Transcript.Write("threeway", "alpha", export.Messages, export.Exchanges);
        var terminal = Terminal(export);

        // The annotation itself, on every message that carries one, asked of the shared derivation.
        var participants = Visibility.Participants(export.Messages);
        var annotations = export.Messages
            .Select(m => Visibility.Describe(m, participants))
            .Where(a => a.Length > 0)
            .ToList();

        Assert.NotEmpty(annotations);

        foreach (var annotation in annotations)
        {
            // **The export carries it and the screen deliberately does not, which is a change and not a
            // drift.** The owner read a broadcast in the room and asked for the list of everyone who did not
            // see each message to go: the header above it already names one sender and one recipient, and the
            // room column beside it lists every participant, so on screen it restated twice over what was
            // already there. A file read somewhere else has neither, which is why the export keeps it.
            Assert.Contains(annotation, console, StringComparison.Ordinal);
            Assert.DoesNotContain(annotation, terminal, StringComparison.Ordinal);
        }
    }

    /// <summary>A conversation of three agents where nobody saw all of it.</summary>
    /// <returns>What the broker exports for it.</returns>
    /// <remarks>
    /// Two pairwise exchanges sharing one conversation. Every message has a participant who was
    /// not party to it, which is the only shape where a visibility annotation says anything —
    /// a two-agent conversation would produce none, and both surfaces would agree vacuously.
    /// </remarks>
    private async Task<ConversationExport> ThreeWayConversationAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, ct);
        await broker.StartAsync(Billing, T0, ct);
        await broker.StartAsync(Inventory, T0, ct);

        await broker.RequestAsync(
            Orders, Billing, "model-a",
            """{"ask":"to billing only"}""",
            T0.AddMinutes(1), "threeway", ct);

        await broker.RequestAsync(
            Orders, Inventory, "model-a",
            """{"ask":"to inventory only"}""",
            T0.AddMinutes(2), "threeway", ct);

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(_broker.Operators).ExportAsync("threeway", ct)
                .ConfigureAwait(false)).ConfigureAwait(false);

        Assert.True(read.Reachable, read.Problem);
        Assert.NotNull(read.Value);

        return read.Value!;
    }

    /// <summary>The terminal's rendering of a conversation, as plain text.</summary>
    /// <param name="export">What the broker returned.</param>
    /// <returns>Everything the terminal would draw.</returns>
    /// <remarks>
    /// Through the region rather than through a helper of this test's own, because the claim is
    /// about what a person sees. The region now writes <c>Transcript.Write</c>'s text — the same
    /// function the console renders from — so the two agreeing is a property of the code rather
    /// than a coincidence two renderers keep having. The assertions below are unchanged from when
    /// they were not, which is what makes them worth keeping: they would still catch a region that
    /// went back to formatting messages itself.
    /// </remarks>
    private static string Terminal(ConversationExport export)
    {
        var region = new TranscriptRegion();

        region.Show("threeway", "alpha", export, null);

        return region.Written;
    }
}
