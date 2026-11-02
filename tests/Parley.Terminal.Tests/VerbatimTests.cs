using Parley.Cli.Tests;
using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That what a person types leaves this surface unchanged, and what comes back is what is made
/// safe.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.8. Run 2.1 asked what text this program did not write can do to
/// the screen, and the answer was the escaping that now sits on every inbound string. This asks the
/// opposite direction, which is a different requirement and is the half that gets confused with it:
/// a surface that neutralised on the way <i>out</i> would write a record of words nobody said, in
/// the one place a person's own words are stored under their own name.
/// </para>
/// <para>
/// The hostile strings are the ones that break a hand-built envelope. SPEC.md §8.1's question is
/// JSON, and a quote, a backslash or a brace in a person's sentence either serialises or corrupts
/// the message around it — and a corrupted envelope is not a rendering bug, it is a forged field.
/// </para>
/// </remarks>
[Integration]
public sealed class VerbatimTests : IAsyncDisposable
{
    /// <summary>Everything that breaks an envelope built by concatenation, in one sentence.</summary>
    private const string Hostile =
        """the totals are "off" by {1}, per C:\srv\orders — and <b>nobody</b> agrees""";

    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public VerbatimTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task A_persons_own_words_reach_the_store_exactly_as_typed()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await TellAsync(driving, shell, Hostile);

        // Read back through the broker, which is the record. Every character of the sentence has to
        // be there, including the ones that would have been escaped away by a careless envelope.
        var said = await ExportedAsync();

        Assert.Contains(Hostile, said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_carries_its_ask_word_for_word()
    {
        // The ask and the conversation name, each of them a place a quote lands. A request refused for
        // shape is the failure this catches at the other end: the broker checks the payload it is handed.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Composing.StartAndSayAsync(
            driving, shell, Orders.Value, "verbatim", "deciding whether " + Hostile + ": is " + Hostile + " true?");

        var said = await ExportedAsync();

        Assert.Contains("deciding whether " + Hostile, said, StringComparison.Ordinal);
        Assert.Contains("is " + Hostile + " true?", said, StringComparison.Ordinal);
        Assert.DoesNotContain("is not an agent name", shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_words_are_made_safe_on_the_way_back_to_the_screen()
    {
        // Both directions in one journey, which is the only way to catch a surface that applied one
        // rule to both: the record keeps the escape character and the screen never receives it.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await TellAsync(driving, shell, "before\u001b[2Jafter");

        Assert.Contains("before\u001b[2Jafter", await ExportedAsync(), StringComparison.Ordinal);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Row(shell.Conversations.List, "verbatim");

        Assert.True(shell.Conversations.OpenSelected());

        await shell.Settled;

        // The shared renderer writes the payload as the JSON it is, so the escape reaches the
        // screen already encoded -- and the sentence is still legible beside it.
        Assert.Contains("before", shell.Transcript.Written, StringComparison.Ordinal);
        Assert.Contains("after", shell.Transcript.Written, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', shell.Transcript.Written);
    }

    [Fact]
    public async Task A_sentence_that_is_only_spaces_is_refused_rather_than_sent()
    {
        // The inverse of the above and the reason the fields validate: an empty message is a row in
        // somebody else's transcript attributed to a person who said nothing.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        Assert.NotNull(Participation.RequiredProblem("   "));

        await Composing.StartAndSayAsync(driving, shell, Orders.Value, "verbatim", "   ");

        Assert.Equal(string.Empty, await ExportedAsync());
    }

    private async Task TellAsync(Driving.Session driving, Shell shell, string words)
    {
        await Composing.StartAndSayAsync(driving, shell, Orders.Value, "verbatim", words);
    }

    private async Task<Shell> SeatedAsync(
        Driving.Session driving, BrokerDirectory directory, SeatBook seats)
    {
        var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        // Seated by opening the project: the broker provides the person seat and the shell takes it, so
        // there is no Join dialog to drive.
        await shell.Settled;

        Assert.NotNull(shell.Held);

        return shell;
    }

    /// <summary>Everything the broker holds for the conversation, decoded back out of its JSON.</summary>
    /// <remarks>
    /// Decoded rather than compared as text, because the encoding is the correct answer and not the
    /// thing under test. A quote arrives as <c>"</c> in the payload and must come back out as a
    /// quote; asserting against the raw JSON would have called a working serialiser a defect.
    /// </remarks>
    private async Task<string> ExportedAsync()
    {
        var export = await new BrokerClient(_broker.Operators)
            .ExportAsync("verbatim", TestContext.Current.CancellationToken);

        var said = new List<string>();

        foreach (var message in export?.Messages ?? [])
        {
            Walk(JsonNode.Parse(message.PayloadJson), said);
        }

        return string.Join("\n", said);
    }

    /// <summary>Every string anywhere in a payload, however deeply nested.</summary>
    private static void Walk(JsonNode? node, List<string> into)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                into.Add(text);
                break;

            case JsonObject o:
                foreach (var pair in o)
                {
                    Walk(pair.Value, into);
                }

                break;

            case JsonArray a:
                foreach (var item in a)
                {
                    Walk(item, into);
                }

                break;

            default:
                break;
        }
    }

    private ProjectSettings Project() =>
        new(ProjectName.Create("alpha"), string.Empty, _broker.Address, null);

    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));
}
