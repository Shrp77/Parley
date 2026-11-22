using Grpc.Net.Client;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// What the shell's own timers do with what they read: the follow loop draws the files placed while the person reads, and
/// the mail poll says a count it could not read is unknown.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both loops are private and run only on a timer</b>, so nothing a key does reaches them, and a guard inside either
/// could be removed with every other test still passing. These drive a real shell against a loopback broker, turn the
/// timer on, and wait, with a deadline, for what the timer alone can change.
/// </para>
/// <para>
/// <b>The follow loop is held on the screen</b>: the transcript's drawn rows. <c>TerminalSurfaceGuardsTests</c> holds the
/// transcript's half, that a follow given a file draws it; this holds the shell's half, that the follow hands it the file.
/// </para>
/// <para>
/// <b>The mail poll is held on the count the status line reads</b>, not on the status line itself. The status line draws
/// only a certain count above zero, so a certain zero and an unknown count draw the same nothing; the difference is what
/// the next reader of the count is told, and only the count says it.
/// </para>
/// </remarks>
[Integration]
public sealed class TheShellsOwnLoopsTests
{
    private const string Conversation = "pricing";

    private static readonly TimeSpan Quickly = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task A_file_placed_while_the_person_reads_is_drawn_in_the_open_transcript()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var files = new FileLocationFixture();

        // Placed elsewhere first, which attaches billing, so the person has somebody to ask.
        await files.PlaceAsync(FileLocationFixture.Billing, "elsewhere.md", "unrelated", "elsewhere");

        using var driving = Driving.Open();
        using var directory = Directory(files.Broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, mailEvery: null);

        await Voice(directory, shell).RequestAsync(
            AgentName.Create(FileLocationFixture.Billing), "who owns the cap?", Conversation, ct);

        // After Driving.Show, which turns following off on every driven surface.
        shell.FollowEvery = Quickly;

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);

        var before = Drawn.Rows(shell.Transcript.List);

        Assert.Contains(before, row => row.Contains("who owns the cap?", StringComparison.Ordinal));
        Assert.DoesNotContain(before, row => row.Contains("cap-notes.md", StringComparison.Ordinal));

        // Placed while the transcript is open: nothing the person does from here on, so only following can draw it.
        await files.PlaceAsync(FileLocationFixture.Billing, "cap-notes.md", "the cap is ours", Conversation);

        await Eventually.UntilAsync(
            () => Drawn.Rows(shell.Transcript.List).Any(row => row.Contains("cap-notes.md", StringComparison.Ordinal)),
            "a file placed while the conversation was open was never drawn in its transcript",
            ct);
    }

    [Fact]
    public async Task A_waiting_count_that_cannot_be_read_is_unknown_rather_than_none()
    {
        var ct = TestContext.Current.CancellationToken;

        using var connection = ParleyDatabase.OpenInMemory();
        var orders = AgentName.Create("orders@test");

        await new AgentRepository(connection).UpsertAsync(
            new AgentRecord(orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active), ct);

        var broker = LoopbackOperator.Start(connection);
        var stopped = false;

        try
        {
            using var driving = Driving.Open();
            using var directory = Directory(broker);
            using var seats = new SeatBook(directory.AgentsFor);
            using var shell = await SeatedAsync(driving, directory, seats, mailEvery: TimeSpan.FromMilliseconds(50));

            // A question waiting on the person, asked by an agent: written through a broker on the same store, as
            // Seated.AsksAsync does.
            var speaking = new Broker.Broker(connection, Parley.Broker.BrokerOptions.Default);

            await speaking.StartAsync(orders, DateTimeOffset.UtcNow, ct);

            var sent = await speaking.RequestAsync(
                orders, shell.Held!.Seat.Name, "model-a", """{"ask":"is the build green?"}""", DateTimeOffset.UtcNow, Conversation, ct);

            Assert.True(sent.Exchange is not null, $"the request was refused: {sent.Admission.Rejection}");

            // While the broker answers, the count is certain and the status line says it.
            await Eventually.UntilAsync(
                () => shell.Mail is { Certain: true, Waiting: 1 },
                "the question waiting on the person was never counted",
                ct);
            await Eventually.UntilAsync(
                () => shell.Status.Contains("1 waiting", StringComparison.Ordinal),
                "the status line never said what was waiting",
                ct);

            stopped = true;
            await broker.DisposeAsync();

            // The poll goes on asking; the broker no longer answers. What it read is not a zero.
            await Eventually.UntilAsync(
                () => shell.Mail is { Certain: false },
                "a count the broker did not answer for was taken as certain",
                ct);

            Assert.Equal((0, false), shell.Mail);
            Assert.DoesNotContain("waiting", shell.Status, StringComparison.Ordinal);
        }
        finally
        {
            if (!stopped)
            {
                await broker.DisposeAsync();
            }
        }
    }

    private static BrokerDirectory Directory(LoopbackOperator broker) =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", broker.Address, null)],
            address => GrpcChannel.ForAddress(address));

    private static SeatVoice Voice(BrokerDirectory directory, Shell shell) =>
        new(directory.AgentsFor(directory.Projects[0]), shell.Held!.Seat);

    /// <summary>A driven shell, seated by opening the project, with the mail poll as asked and following off.</summary>
    private static async Task<Shell> SeatedAsync(
        Driving.Session driving, BrokerDirectory directory, SeatBook seats, TimeSpan? mailEvery)
    {
        var shell = new Shell(directory) { Seats = seats, MailEvery = mailEvery, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        Assert.NotNull(shell.Held);

        return shell;
    }
}
