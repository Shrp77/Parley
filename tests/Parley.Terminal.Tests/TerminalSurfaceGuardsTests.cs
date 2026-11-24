using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>Lens 3 sweep on 3b752970: what the status line offers, what a followed transcript draws, and what is said.</summary>
/// <remarks>The surface has no seat book and its broker is a dead port, as in <c>ReadingNeedsNoSeatTests</c>.</remarks>
[Integration]
public sealed class TerminalSurfaceGuardsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-terminal-guards-" + Guid.NewGuid().ToString("N"));

    public TerminalSurfaceGuardsTests() => System.IO.Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static ConversationExport Saying(string words) =>
        new(
            [
                new Message(
                    1, 7, MessageType.Request, AgentName.Create("orders@test"), AgentName.Create("billing@test"), "claude",
                    "{\"ask\":\"" + words + "\"}", "release-notes", DateTimeOffset.UnixEpoch, null),
            ],
            [],
            null);

    private static void FocusOn<TPane>(Shell shell)
    {
        var at = shell.Panes.ToList().FindIndex(pane => pane is TPane);

        Assert.True(at >= 0, $"no pane here is a {typeof(TPane).Name}");

        shell.Focus(at);
    }

    [Fact]
    public async Task The_reference_key_is_offered_named_for_its_exchange_only_on_a_message_that_names_one()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, "release-notes");

        shell.Transcript.Show("release-notes", "alpha", Saying("See exchange #42 for the cap."), null);
        driving.App.LayoutAndDraw(true);
        FocusOn<TranscriptRegion>(shell);
        Assert.True(shell.Transcript.Reach(1));

        Assert.Contains(shell.Actions(), action => action.Label == "Open exchange #42");

        shell.Transcript.Show("release-notes", "alpha", Saying("Nothing is named here."), null);
        driving.App.LayoutAndDraw(true);
        Assert.True(shell.Transcript.Reach(1));

        Assert.DoesNotContain(
            shell.Actions(),
            action => action.Label == Shell.ReferenceTitle || action.Label.StartsWith("Open exchange", StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_an_empty_list_with_no_seat_neither_media_nor_the_still_open_question_is_offered()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);
        FocusOn<ConversationsRegion>(shell);

        Assert.Null(shell.Held);
        Assert.Null(shell.Conversations.Selected);
        Assert.DoesNotContain(shell.Actions(), action => action.Label == Media.Title);
        Assert.DoesNotContain(shell.Actions(), action => action.Label == Shell.StillOpenTitle);
    }

    [Fact]
    public async Task An_inbox_the_broker_cannot_answer_says_so_and_opens_nothing()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);
        FocusOn<ConversationsRegion>(shell);

        await driving.PressThenSettleAsync(new Key(Shell.InboxKey).WithAlt, () => shell.Settled);

        Assert.StartsWith("Could not read the Inbox: ", shell.Said, StringComparison.Ordinal);
        Assert.Null(shell.InboxShown);
    }

    [Fact]
    public async Task A_file_placed_while_a_conversation_is_followed_is_drawn_where_it_was_placed()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, "release-notes");

        shell.Transcript.Show("release-notes", "alpha", Saying("Who owns the cap?"), null, []);
        Assert.DoesNotContain("cap-notes.md", shell.Transcript.Written, StringComparison.Ordinal);

        byte[] bytes = [1, 2, 3];
        var file = new SharedFile(
            "release-notes", "release-notes", SharedFileName.Create("cap-notes.md"), bytes.Length, ContentHash.Of(bytes),
            AgentName.Create("orders@test"), DateTimeOffset.UnixEpoch.AddMinutes(-1));

        Assert.True(shell.Transcript.Follow("release-notes", "alpha", Saying("Who owns the cap?"), [file]));
        Assert.Contains("cap-notes.md", shell.Transcript.Written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workspace_whose_git_leads_off_this_machine_is_refused_as_off_this_machine()
    {
        // The workspace is a local folder; only its .git is a link to a share. Without the second half of the first check it
        // was refused as "not a repository", which sends the person looking for a .git that is there.
        var workspace = System.IO.Directory.CreateDirectory(Path.Combine(_root, "orders")).FullName;
        var host = "192.0.2." + Random.Shared.Next(10, 250).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var target = OperatingSystem.IsWindows() ? $@"\\{host}\share\git" : $"//{host}/share/git";

        try
        {
            System.IO.Directory.CreateSymbolicLink(Path.Combine(workspace, ".git"), target);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"this machine cannot make a link here: {refused.Message}");
        }

        Assert.Equal(LocalPath.OffThisMachine, Adding.WorkspaceProblem(workspace));
    }
}
