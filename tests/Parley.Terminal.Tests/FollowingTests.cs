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
[Integration]
public sealed class FollowingTests
{
    private const string Conversation = "release-notes";

    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static Message Question(long id, string text) =>
        new(
            id,
            7,
            MessageType.Request,
            Orders,
            Billing,
            "claude",
            "{\"ask\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}",
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
    public async Task A_reader_who_has_not_moved_is_carried_to_the_new_end()
    {
        // **Reversed on 2026-10-01, on the owner's call: a conversation taking place is tailed.** This asserted the
        // opposite since 0.25.7 — that a reader who never touched the cursor was reading from the top. Nothing on
        // screen is waited on more than the next thing said, so no row now counts as being at the end.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        await ReadingAsync(driving, shell, Holding(Question(1, "who owns the cap?")));

        var list = shell.Transcript.List;

        list.SelectedItem = null;

        Assert.True(shell.Transcript.Follow(
            Conversation, "alpha", Holding(Question(1, "who owns the cap?"), Answer(2, "the billing team owns it"))));

        Assert.Equal(list.Source!.Count - 1, list.SelectedItem);
    }

    [Fact]
    public async Task A_reader_scrolled_back_with_the_wheel_is_not_moved_at_all()
    {
        // **The owner's nuance, 2026-10-01: at the bottom, tail; anywhere else, do not even move the reader.** The wheel
        // scrolls the view and leaves the cursor where it was, so a rule read off the cursor would think somebody reading
        // the start of a conversation was still at the end and carry them away from it. What decides is what is on screen.
        using var driving = Driving.Open(width: 100, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        var long_ = Enumerable.Range(1, 30).Select(id => Question(id, $"question number {id}?")).ToArray();

        await ReadingAsync(driving, shell, Holding(long_));

        var list = shell.Transcript.List;

        Assert.True(list.Source!.Count > list.Viewport.Height, "the conversation fits on screen, so nothing can scroll");

        list.Viewport = list.Viewport with { Y = 0 };

        var selected = list.SelectedItem;

        Assert.True(shell.Transcript.Follow(
            Conversation, "alpha", Holding([.. long_, Answer(31, "the last answer")])));

        driving.App.LayoutAndDraw(true);

        Assert.Equal(0, list.Viewport.Y);
        Assert.Equal(selected, list.SelectedItem);
    }

    [Fact]
    public async Task A_reader_at_the_bottom_sees_the_new_bottom()
    {
        // The other half: on screen at the end means the newest message is what is shown next.
        using var driving = Driving.Open(width: 100, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        var long_ = Enumerable.Range(1, 30).Select(id => Question(id, $"question number {id}?")).ToArray();

        await ReadingAsync(driving, shell, Holding(long_));

        var list = shell.Transcript.List;

        Assert.True(shell.Transcript.Follow(
            Conversation, "alpha", Holding([.. long_, Answer(31, "the last answer")])));

        driving.App.LayoutAndDraw(true);

        Assert.True(
            list.Viewport.Y + list.Viewport.Height >= list.Source!.Count,
            $"rows {list.Viewport.Y}..{list.Viewport.Y + list.Viewport.Height} of {list.Source!.Count} are on screen, not the end");
    }

    [Fact]
    public async Task A_conversation_opens_at_its_end()
    {
        // Somebody opening a conversation is there for what has just been said, which is the bottom of it.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        await ReadingAsync(
            driving,
            shell,
            Holding(Question(1, "who owns the cap?"), Answer(2, "the billing team owns it"), Question(3, "and the floor?")));

        var list = shell.Transcript.List;

        Assert.Equal(list.Source!.Count - 1, list.SelectedItem);
        Assert.Contains("and the floor?", string.Join("\n", Drawn.Rows(list)), StringComparison.Ordinal);
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
