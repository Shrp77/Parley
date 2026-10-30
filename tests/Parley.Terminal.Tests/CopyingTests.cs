using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a conversation can leave this screen, and that the suite never touches a real clipboard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked for by the owner while reading a transcript.</b> A terminal is where a conversation is
/// read and not where it is acted on: what comes next is quoting it somewhere — to a colleague, or
/// into the session of whichever agent is going to do something about it — and re-typing it is not
/// a workflow. <c>parley export</c> exists and is a different machine's shell away when the
/// terminal is open over SSH.
/// </para>
/// <para>
/// <b>What is copied is what is on the screen</b>, not what the export writes. The export flattens
/// every payload onto one line because a text file has no defence against a payload shaped like an
/// annotation; this surface has the indent instead, so the readable form is also the safe one.
/// </para>
/// </remarks>
[Integration]
public sealed class CopyingTests
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

    private static async Task<Shell> OnAConversationAsync(Driving.Session driving, Shell shell)
    {
        await Walking.GoAsync(shell, Viewing.Conversation, "release-notes");

        shell.Transcript.Show("release-notes", "alpha", Conversation(), null);

        driving.App.LayoutAndDraw(true);

        return shell;
    }

    [Fact]
    public async Task The_conversation_on_screen_is_what_goes_on_the_clipboard()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await OnAConversationAsync(driving, shell);

        await shell.Copy();

        var copied = Assert.Single(driving.Copied);

        Assert.Equal(shell.Transcript.Written, copied);
        Assert.Contains("orders@test -> billing@test", copied, StringComparison.Ordinal);
        Assert.Contains("who owns the cap?", copied, StringComparison.Ordinal);

        // And it says so, with the size: a clipboard gives no other evidence that a key did
        // anything at all.
        Assert.Equal(Shell.Copied(copied.Length), shell.Said);
    }

    [Fact]
    public async Task The_key_copies_it_and_the_action_is_named()
    {
        // contracts/surface.md: no action exists only as a key, and the menu is the only route a
        // pointer has to one.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await OnAConversationAsync(driving, shell);

        // Named rather than counted. The conversation view gained a second pane in 0.30.0 and an index
        // silently became a different region; what this test is about is the transcript.
        shell.Focus(shell.Panes.ToList().FindIndex(p => p is TranscriptRegion));

        Assert.Contains(shell.Actions(), action => action.Label == Shell.CopyTitle);

        driving.Press(new Key(Shell.CopyKey).WithAlt);

        Assert.Single(driving.Copied);
    }

    [Fact]
    public async Task It_is_offered_without_a_seat()
    {
        // The operator most likely to be quoting a transcript somewhere else is the one who has
        // not joined: they are reading rather than taking part. Every other action here needs a
        // seat, and this arm used to be gated as a whole.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await OnAConversationAsync(driving, shell);

        // Named rather than counted. The conversation view gained a second pane in 0.30.0 and an index
        // silently became a different region; what this test is about is the transcript.
        shell.Focus(shell.Panes.ToList().FindIndex(p => p is TranscriptRegion));

        Assert.Null(shell.Held);
        Assert.Contains(shell.Actions(), action => action.Label == Shell.CopyTitle);
    }

    [Fact]
    public async Task Nothing_is_offered_with_no_conversation_open()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await Walking.GoAsync(shell, Viewing.Conversation, "release-notes");

        shell.Transcript.Show("release-notes", "alpha", null, "the broker did not answer");

        // Named rather than counted. The conversation view gained a second pane in 0.30.0 and an index
        // silently became a different region; what this test is about is the transcript.
        shell.Focus(shell.Panes.ToList().FindIndex(p => p is TranscriptRegion));

        Assert.DoesNotContain(shell.Actions(), action => action.Label == Shell.CopyTitle);

        // And the action itself refuses rather than copying an error message.
        await shell.Copy();

        Assert.Empty(driving.Copied);
    }

    [Fact]
    public async Task A_clipboard_that_will_not_take_it_says_where_else_to_look()
    {
        // A clipboard is an OS integration: absent on a bare Linux box, and over SSH it is the
        // server's rather than the one in front of the person. Reported, with the way round it.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await OnAConversationAsync(driving, shell);

        shell.Copier = (_, _) => false;

        await shell.Copy();

        Assert.Equal(Shell.NotCopied, shell.Said);
        // The way round it is one that works. It named `parley export` until the combined review gate, which the command
        // line has refused since 019 -- sending the person to a door that sends them back here.
        Assert.Contains("F8", shell.Said, StringComparison.Ordinal);
        Assert.DoesNotContain("parley export", shell.Said, StringComparison.Ordinal);
    }
}
