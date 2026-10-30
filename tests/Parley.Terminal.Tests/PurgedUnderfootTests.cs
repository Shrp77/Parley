using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// An agent purged while the person is in a conversation it took part in.
/// </summary>
/// <remarks>
/// <b>Found by review, round 15.</b> A purge destroyed the conversation without recording it, so the follow showed it as
/// one nothing was said in, and the line in the box opened a new conversation under the same identifier with the agents
/// left. A destroy keeps the identifier taken for exactly that reason (SPEC.md §14), and a purge now records it as one.
/// </remarks>
[Integration]
public sealed class PurgedUnderfootTests : IDisposable
{
    private const string Conversation = "release-notes";

    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly TimeSpan Quickly = TimeSpan.FromMilliseconds(200);

    private readonly SqliteConnection _connection;

    public PurgedUnderfootTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        foreach (var name in new[] { Orders, Billing })
        {
            new AgentRepository(_connection).UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_purge_under_an_open_conversation_is_said_rather_than_shown_as_empty()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var broker = LoopbackOperator.Start(_connection);
        using var driving = Driving.Open();
        using var directory = new BrokerDirectory(
            [new ProjectSettings(Walking.Alpha, "unused.db", broker.Address, null)],
            _ => GrpcChannel.ForAddress(broker.Address));
        using var seats = new SeatBook(directory.AgentsFor);
        var shell = new Shell(directory) { Seats = seats };
        using var _ = shell;

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        var voice = new SeatVoice(directory.AgentsFor(directory.Projects[0]), shell.Held!.Seat);
        await voice.RequestAsync(Orders, "the first thing", Conversation, ct);
        await voice.RequestAsync(Billing, "the second thing", Conversation, ct);

        shell.FollowEvery = Quickly;
        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.OpenAsync(directory.Projects[0], Conversation, ct);
        Assert.Contains("the first thing", shell.Transcript.Written, StringComparison.Ordinal);

        shell.Line.Written = "one more thing";

        var purged = await new BrokerClient(broker.Operators).PurgeAsync(Orders, ct);
        Assert.Equal(PurgeVerdict.Gone, purged.Verdict);

        await Task.Delay(Quickly * 8, ct);
        await shell.Settled;

        // Said as destroyed, which a purge is (SPEC.md §14), not shown as a conversation nothing was said in.
        Assert.DoesNotContain(TranscriptRegion.Empty, shell.Transcript.Written, StringComparison.Ordinal);
        Assert.Contains(TranscriptRegion.Destroyed, shell.Transcript.Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_written_before_a_purge_does_not_open_the_purged_conversation_again()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var broker = LoopbackOperator.Start(_connection);
        using var driving = Driving.Open();
        using var directory = new BrokerDirectory(
            [new ProjectSettings(Walking.Alpha, "unused.db", broker.Address, null)],
            _ => GrpcChannel.ForAddress(broker.Address));
        using var seats = new SeatBook(directory.AgentsFor);
        var shell = new Shell(directory) { Seats = seats };
        using var _ = shell;

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        var voice = new SeatVoice(directory.AgentsFor(directory.Projects[0]), shell.Held!.Seat);
        await voice.RequestAsync(Orders, "the first thing", Conversation, ct);
        await voice.RequestAsync(Billing, "the second thing", Conversation, ct);

        shell.FollowEvery = Quickly;
        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.OpenAsync(directory.Projects[0], Conversation, ct);
        shell.Line.Written = "one more thing";

        Assert.Equal(PurgeVerdict.Gone, (await new BrokerClient(broker.Operators).PurgeAsync(Orders, ct)).Verdict);

        await Task.Delay(Quickly * 8, ct);
        await shell.Settled;

        shell.Line.Accept();
        await shell.Settled;

        // Sent to billing as the first message of a new conversation under the purged one's identifier.
        var after = await new BrokerClient(broker.Operators).ExportAsync(Conversation, ct);
        Assert.True(after is null || after.Messages.Count == 0, $"Reopened; the message line said: {shell.Said}");
    }
}
