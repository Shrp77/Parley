using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the terminal tells a destroyed conversation apart from an empty one.
/// </summary>
/// <remarks>
/// The distinction is the whole of FR-006 on this surface. A conversation with no messages is one
/// nothing was said in; a destroyed one is one whose messages were taken away. Saying the first for
/// the second leaves an operator unable to tell a destroy from a store that lost something — which
/// is the failure §14 keeps a record to prevent, and the record is no use if the screen does not
/// read it.
/// </remarks>
[Integration]
public sealed class DestroyedTranscriptTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static DestroyedConversation Record() =>
        new(
            "a-discussion",
            new DateTimeOffset(2026, 1, 2, 9, 30, 0, TimeSpan.Zero),
            3,
            [AgentName.Create("orders@test"), AgentName.Create("billing@test")]);

    [Fact]
    public async Task A_destroyed_conversation_says_so_rather_than_saying_it_is_empty()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, "a-discussion");

        shell.Transcript.Show(
            "a-discussion", "alpha", new ConversationExport([], [], Record()), null);

        var written = shell.Transcript.Written;

        Assert.Contains(TranscriptRegion.Destroyed, written, StringComparison.Ordinal);
        Assert.DoesNotContain(TranscriptRegion.Empty, written, StringComparison.Ordinal);
        Assert.Contains("3 messages", written, StringComparison.Ordinal);
        Assert.Contains("orders@test", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_with_no_messages_still_says_it_is_empty()
    {
        // The other side of the distinction. Widening the destroyed message to cover both would be
        // the same collapse in the opposite direction.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        shell.Transcript.Show("a-discussion", "alpha", new ConversationExport([], []), null);

        Assert.Contains(TranscriptRegion.Empty, shell.Transcript.Written, StringComparison.Ordinal);
        Assert.DoesNotContain(
            TranscriptRegion.Destroyed, shell.Transcript.Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_that_did_not_answer_is_not_reported_as_a_destroy()
    {
        // Three states, not two: destroyed, empty, and unknown. Reporting the third as either of
        // the first is the containment rule 005 wrote, applied to a new answer.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        shell.Transcript.Show("a-discussion", "alpha", null, "the broker is not answering");

        var written = shell.Transcript.Written;

        Assert.Contains("not answering", written, StringComparison.Ordinal);
        Assert.DoesNotContain(TranscriptRegion.Destroyed, written, StringComparison.Ordinal);
        Assert.DoesNotContain(TranscriptRegion.Empty, written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_record_reaches_the_screen_neutralised_like_anything_else()
    {
        // Every value in it was written by somebody else: the identifier by an agent, the
        // participants by their own adapters. 005's rule that nothing an agent wrote may repaint
        // the screen an operator decides on does not stop applying because the conversation is gone.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        var hostile = new DestroyedConversation(
            "a-discussion[31m",
            DateTimeOffset.UnixEpoch,
            1,
            [AgentName.Create("orders@test")]);

        shell.Transcript.Show(
            "a-discussion", "alpha", new ConversationExport([], [], hostile), null);
        driving.App.LayoutAndDraw(true);

        Assert.DoesNotContain("", shell.Transcript.Written, StringComparison.Ordinal);
    }
}
