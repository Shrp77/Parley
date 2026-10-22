using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// What the surface does when the thing it is showing stops existing.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.8. T035 gave the conversation view a fourth state, and the
/// interesting case is not rendering it but arriving at it: a person is reading a conversation, an
/// operator elsewhere destroys it, and the next read returns a record where a transcript was.
/// </para>
/// <para>
/// Two deployments share a broker often enough for this to be ordinary — the console and the
/// terminal, or two people. The failure to look for is a view left about something that is gone,
/// with actions still offered on it.
/// </para>
/// </remarks>
public sealed class DestroyedNavigationTests
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
            [AgentName.Create("orders@test")]);

    [Fact]
    public async Task Reading_a_conversation_that_has_been_destroyed_still_lets_you_leave()
    {
        // The one that matters. A view showing a record is still a view, and Escape has to work
        // from it -- otherwise somebody who arrives here is stuck looking at a thing that is gone.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, "a-discussion");

        shell.Transcript.Show(
            "a-discussion", "alpha", new ConversationExport([], [], Record()), null);

        driving.Press(global::Terminal.Gui.Input.Key.Esc);
        await shell.Settled;

        Assert.Equal(Viewing.Project, shell.Place.Viewing);
    }

    [Fact]
    public async Task The_conversation_view_offers_nothing_that_speaks_into_a_destroyed_one()
    {
        // Taking part needs a seat and there is none here, so what is asserted is the weaker and
        // still useful thing: the actions offered do not include answering or asking. A surface
        // that offered to answer into a conversation that no longer exists would be offering to
        // write somewhere nothing can be written.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, "a-discussion");

        shell.Transcript.Show(
            "a-discussion", "alpha", new ConversationExport([], [], Record()), null);

        Assert.DoesNotContain(shell.Actions(), a => a.Label == Participation.ResultTitle);
        Assert.DoesNotContain(shell.Actions(), a => a.Label == Participation.RequestTitle);
    }

    [Fact]
    public async Task Opening_another_conversation_after_a_destroyed_one_replaces_what_is_shown()
    {
        // The record is not sticky. A transcript region that kept it while showing a different
        // conversation would attribute a destroy to the wrong subject.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, "a-discussion");

        shell.Transcript.Show(
            "a-discussion", "alpha", new ConversationExport([], [], Record()), null);

        Assert.Contains(TranscriptRegion.Destroyed, shell.Transcript.Written, StringComparison.Ordinal);

        shell.Transcript.Show("another", "alpha", new ConversationExport([], []), null);

        Assert.DoesNotContain(
            TranscriptRegion.Destroyed, shell.Transcript.Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clearing_the_transcript_clears_the_record_with_it()
    {
        // Clear is what a change of project calls. A record surviving it would show one project's
        // destroy while somebody was looking at another.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, "a-discussion");

        shell.Transcript.Show(
            "a-discussion", "alpha", new ConversationExport([], [], Record()), null);

        shell.Transcript.Clear();

        Assert.DoesNotContain(
            TranscriptRegion.Destroyed, shell.Transcript.Written, StringComparison.Ordinal);
        Assert.Null(shell.Transcript.Open);
    }

    [Fact]
    public async Task The_record_survives_a_resize_without_being_re_laid_into_nonsense()
    {
        // The transcript re-wraps from the written text on every width change, and the record is
        // written text like any other. What must not happen is a second wrap of an already-wrapped
        // line, which is the lossy round trip the region was built to avoid.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, "a-discussion");

        shell.Transcript.Show(
            "a-discussion", "alpha", new ConversationExport([], [], Record()), null);

        var before = shell.Transcript.Written;

        shell.Transcript.Resize(40);
        shell.Transcript.Resize(200);

        Assert.Equal(before, shell.Transcript.Written);
    }
}
