using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That an open transcript shows what arrives, without moving what somebody is reading.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported by the owner.</b> They asked an agent a question from the terminal, the answer arrived
/// forty-seven seconds later, and the transcript went on showing the question alone until they closed it
/// and opened it again. It was read once, when it was opened, and never again.
/// </para>
/// <para>
/// These drive <see cref="TranscriptRegion.Follow"/> directly with the conversation as the broker would
/// return it, because what they check is what a redraw does to the screen and the reader's place in it.
/// That the shell reads it again on its own is checked against a real broker elsewhere.
/// </para>
/// </remarks>
public sealed class FollowingTests
{
    private const string Conversation = "release-notes";

    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static Message Question(long id, string text) =>
        new(
            id,
            7,
            MessageType.Request,
            Orders,
            Billing,
            "claude",
            "{\"intent\":\"decide\",\"items\":[{\"id\":\"q1\",\"kind\":\"question\",\"text\":"
            + System.Text.Json.JsonSerializer.Serialize(text) + "}],\"done_when\":\"the owner of the cap is named\"}",
            Conversation,
            DateTimeOffset.UnixEpoch.AddMinutes(id),
            null);

    private static Message Answer(long id, string text) =>
        new(
            id,
            7,
            MessageType.Result,
            Billing,
            Orders,
            "claude",
            "{\"outcomes\":[{\"id\":\"q1\",\"outcome\":\"answered\",\"text\":"
            + System.Text.Json.JsonSerializer.Serialize(text) + "}]}",
            Conversation,
            DateTimeOffset.UnixEpoch.AddMinutes(id),
            null);

    private static ConversationExport Holding(params Message[] messages) => new([.. messages], [], null);

    private static async Task<Shell> ReadingAsync(Driving.Session driving, Shell shell, ConversationExport export)
    {
        driving.Show(shell);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);

        shell.Transcript.Show(Conversation, "alpha", export, null);

        driving.App.LayoutAndDraw(true);

        return shell;
    }

    [Fact]
    public async Task An_answer_that_arrives_is_drawn_without_reopening_the_transcript()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        await ReadingAsync(driving, shell, Holding(Question(1, "who owns the cap?")));

        Assert.DoesNotContain("the billing team owns it", shell.Transcript.Written, StringComparison.Ordinal);

        var redrawn = shell.Transcript.Follow(
            Conversation, "alpha", Holding(Question(1, "who owns the cap?"), Answer(2, "the billing team owns it")));

        Assert.True(redrawn);
        Assert.Contains("the billing team owns it", shell.Transcript.Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_new_redraws_nothing()
    {
        // Every tick is a read, and most of them find nothing. A redraw on each would flicker a screen
        // that has not changed, and reset whatever the list does on a new source.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        await ReadingAsync(driving, shell, Holding(Question(1, "who owns the cap?")));

        Assert.False(shell.Transcript.Follow(Conversation, "alpha", Holding(Question(1, "who owns the cap?"))));
    }

    [Fact]
    public async Task A_reader_further_up_is_left_where_they_are()
    {
        // The web console's objection to live transcripts, answered: a page that changes under somebody
        // mid-sentence. A transcript grows only at the end, so the row under the cursor stays that row.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        await ReadingAsync(
            driving,
            shell,
            Holding(Question(1, "who owns the cap?"), Answer(2, "the billing team owns it"), Question(3, "and the floor?")));

        var list = shell.Transcript.List;

        list.SelectedItem = 1;

        var before = list.Source!.ToList()[1];

        Assert.True(shell.Transcript.Follow(
            Conversation,
            "alpha",
            Holding(
                Question(1, "who owns the cap?"),
                Answer(2, "the billing team owns it"),
                Question(3, "and the floor?"),
                Answer(4, "there is no floor"))));

        Assert.Equal(1, list.SelectedItem);
        Assert.Equal(before, list.Source!.ToList()[1]);
    }

    [Fact]
    public async Task A_reader_on_the_last_row_is_kept_on_the_last_row()
    {
        // Somebody at the end of a conversation is reading it as it arrives. Left on what used to be the
        // last row, they would have to go looking for what they are waiting for.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        await ReadingAsync(driving, shell, Holding(Question(1, "who owns the cap?")));

        var list = shell.Transcript.List;
        var rows = list.Source!.Count;

        list.SelectedItem = rows - 1;

        Assert.True(shell.Transcript.Follow(
            Conversation, "alpha", Holding(Question(1, "who owns the cap?"), Answer(2, "the billing team owns it"))));

        Assert.True(list.Source!.Count > rows, "the answer added no rows");
        Assert.Equal(list.Source!.Count - 1, list.SelectedItem);
    }

    [Fact]
    public async Task A_reader_who_has_not_moved_is_not_carried_to_the_bottom()
    {
        // Nothing says a reader who never touched the cursor is waiting at the end; they are more likely
        // reading from the top, which is where the transcript opens.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        await ReadingAsync(driving, shell, Holding(Question(1, "who owns the cap?")));

        var list = shell.Transcript.List;

        list.SelectedItem = null;

        Assert.True(shell.Transcript.Follow(
            Conversation, "alpha", Holding(Question(1, "who owns the cap?"), Answer(2, "the billing team owns it"))));

        Assert.Null(list.SelectedItem);
    }

    [Fact]
    public async Task A_read_of_a_conversation_the_reader_has_left_is_not_drawn()
    {
        // The read was queued while one conversation was open and finished after another was. Drawing it
        // would put the wrong conversation's messages under the right one's name.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        await ReadingAsync(driving, shell, Holding(Question(1, "who owns the cap?")));

        var written = shell.Transcript.Written;

        Assert.False(shell.Transcript.Follow(
            "some-other-conversation", "alpha", Holding(Question(1, "who owns the cap?"), Answer(2, "not yours"))));

        Assert.Equal(written, shell.Transcript.Written);
    }

    [Fact]
    public void Production_follows_and_a_driven_surface_does_not()
    {
        // Both halves, as LaunchTests holds the browser seam to: the shipped default reads again, and the
        // harness turns it off so the suite's assertions do not race a timer.
        using var directory = Directory();

        using (var shipped = new Shell(directory))
        {
            Assert.NotNull(shipped.FollowEvery);
        }

        using var driving = Driving.Open();
        using var driven = new Shell(directory);

        driving.Show(driven);

        Assert.Null(driven.FollowEvery);
    }

    [Fact]
    public void A_failed_read_is_said_in_words_that_do_not_claim_the_screen_is_current()
    {
        var said = Shell.NotFollowing("the broker did not answer");

        Assert.Contains("may be out of date", said, StringComparison.Ordinal);
        Assert.Contains("the broker did not answer", said, StringComparison.Ordinal);
    }
}
