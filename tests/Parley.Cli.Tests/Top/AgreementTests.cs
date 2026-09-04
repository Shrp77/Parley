using Microsoft.Data.Sqlite;
using Parley.Cli.Top;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Core;
using Parley.Store;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace Parley.Cli.Tests.Top;

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

        // The annotation itself, on every message that carries one. Asked of the shared
        // derivation and then asserted of both outputs -- if either surface dropped it, or
        // computed its own, this is where the two would part company.
        var participants = Visibility.Participants(export.Messages);
        var annotations = export.Messages
            .Select(m => Visibility.Describe(m, participants))
            .Where(a => a.Length > 0)
            .ToList();

        Assert.NotEmpty(annotations);

        foreach (var annotation in annotations)
        {
            Assert.Contains(annotation, console, StringComparison.Ordinal);
            Assert.Contains(annotation, terminal, StringComparison.Ordinal);
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

        await broker.NotifyAsync(
            Orders, Billing, "model-a", """{"text":"to billing only"}""",
            T0.AddMinutes(1), "threeway", ct);

        await broker.NotifyAsync(
            Orders, Inventory, "model-a", """{"text":"to inventory only"}""",
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
    private static string Terminal(ConversationExport export)
    {
        var participants = Visibility.Participants(export.Messages);
        var states = new Dictionary<long, ExchangeSnapshot>();

        foreach (var exchange in export.Exchanges)
        {
            states[exchange.ExchangeId] = exchange;
        }

        var written = new StringWriter();

        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(written),
        });

        // Wide enough that nothing wraps. A name broken across two lines would fail a Contains
        // that has nothing to do with agreement, which would make this test report the wrong
        // thing when it eventually failed.
        console.Profile.Width = 400;

        foreach (var message in export.Messages)
        {
            console.Write((IRenderable)TranscriptView.Panel(message, participants, states, console.Profile.Width));
        }

        return written.ToString();
    }
}
