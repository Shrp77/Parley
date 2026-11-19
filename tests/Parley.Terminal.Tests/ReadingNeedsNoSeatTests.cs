using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That what only reads is offered to a person who holds no seat: a conversation's Media and the Inbox.
/// </summary>
/// <remarks>
/// The surface is built with no seat book, so it never holds one, and the broker is a dead port: what is held here is
/// whether the action is offered where the person is, which is what the status line and the menus read.
/// </remarks>
[Integration]
[Slow]
public sealed class ReadingNeedsNoSeatTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static ConversationExport Conversation() =>
        new(
            [
                new Message(
                    1,
                    7,
                    MessageType.Request,
                    AgentName.Create("orders@test"),
                    AgentName.Create("billing@test"),
                    "claude",
                    "{\"ask\":\"who owns the cap?\"}",
                    "release-notes",
                    DateTimeOffset.UnixEpoch,
                    null),
            ],
            [],
            null);

    private static async Task OnAConversationAsync(Driving.Session driving, Shell shell)
    {
        await Walking.GoAsync(shell, Viewing.Conversation, "release-notes");

        shell.Transcript.Show("release-notes", "alpha", Conversation(), null);

        driving.App.LayoutAndDraw(true);
    }

    private static void FocusOn<TPane>(Shell shell)
    {
        var at = shell.Panes.ToList().FindIndex(pane => pane is TPane);

        Assert.True(at >= 0, $"no pane here is a {typeof(TPane).Name}");

        shell.Focus(at);
    }

    [Fact]
    public async Task A_conversations_media_is_offered_in_the_transcript_without_a_seat()
    {
        // Without the guard Media asked for a seat, so a person reading a transcript with none could not see the images
        // placed in it, though looking at them speaks as nobody (review, lens 3 round 35).
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await OnAConversationAsync(driving, shell);

        FocusOn<TranscriptRegion>(shell);

        Assert.Null(shell.Held);
        Assert.Contains(shell.Actions(), action => action.Label == Media.Title);
    }

    [Fact]
    public async Task The_inbox_is_offered_on_the_conversations_and_in_the_room_without_a_seat()
    {
        // Without the guard the Inbox asked for a seat, so a person with none could not read what waits on them, though
        // reading it speaks as nobody (review, lens 3 round 35).
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await Walking.GoAsync(shell, Viewing.Project);

        FocusOn<ConversationsRegion>(shell);

        Assert.Null(shell.Held);
        Assert.Contains(shell.Actions(), action => action.Label == Shell.InboxTitle);

        await OnAConversationAsync(driving, shell);

        FocusOn<RoomRegion>(shell);

        Assert.Null(shell.Held);
        Assert.Contains(shell.Actions(), action => action.Label == Shell.InboxTitle);
    }
}
