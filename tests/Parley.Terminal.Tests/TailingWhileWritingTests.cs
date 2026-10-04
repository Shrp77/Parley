using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That writing a reply does not move the reader's place in the conversation above it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's report of 2026-10-04</b>: at the bottom of a conversation, they replied, and the transcript did not
/// tail what was said next. Writing a long reply grows the box, which takes rows from the transcript, and in
/// Terminal.Gui 2.4.17 a list whose frame changes has its scrollbar put the view back at the first row. The transcript
/// then judged, from the screen, that a reader who had not moved was reading the start of the conversation, and stopped
/// carrying them to each new message.
/// </para>
/// <para>
/// Read off the laid-out screen, because the defect is in a layout and nothing short of one shows it.
/// </para>
/// </remarks>
public sealed class TailingWhileWritingTests
{
    private const string Conversation = "release-notes";

    private const string LongReply =
        "the build is green on my side the build is green on my side the build is green on my side "
        + "the build is green on my side the build is green on my side the build is green on my side "
        + "the build is green on my side the build is green on my side the build is green on my side";

    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private static Message Said(long id, string text) =>
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

    private static ConversationExport Holding(IEnumerable<Message> messages) => new([.. messages], [], null);

    private static string Where(global::Terminal.Gui.Views.ListView list) =>
        $"rows {list.Viewport.Y}..{list.Viewport.Y + list.Viewport.Height} of {list.Source!.Count}";

    private static bool AtTheEnd(global::Terminal.Gui.Views.ListView list) =>
        list.Viewport.Y + list.Viewport.Height >= list.Source!.Count;

    private static async Task<(Driving.Session Driving, BrokerDirectory Directory, Shell Shell, List<Message> Said)> OpenAsync()
    {
        var driving = Driving.Open(width: 100, height: 30);
        var directory = new BrokerDirectory(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
        var shell = new Shell(directory);
        var said = Enumerable.Range(1, 30).Select(id => Said(id, $"message number {id}")).ToList();

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        shell.Transcript.Show(Conversation, "alpha", Holding(said), null);
        driving.App.LayoutAndDraw(true);

        return (driving, directory, shell, said);
    }

    private static async Task WriteAsync(Driving.Session driving, Shell shell, string text)
    {
        shell.Line.Written = text;
        await shell.Settled;
        driving.App.LayoutAndDraw(true);
    }

    [Fact]
    public async Task A_reader_at_the_end_who_writes_a_long_reply_still_sees_what_arrives()
    {
        var (driving, directory, shell, said) = await OpenAsync();

        using (driving)
        using (directory)
        using (shell)
        {
            var list = shell.Transcript.List;

            Assert.True(AtTheEnd(list), "the conversation did not open at its end");

            await WriteAsync(driving, shell, LongReply);

            Assert.True(AtTheEnd(list), $"the box grew and the reader was moved: {Where(list)}");

            said.Add(Said(31, "the first thing said meanwhile"));
            shell.Transcript.Follow(Conversation, "alpha", Holding(said));
            driving.App.LayoutAndDraw(true);

            Assert.True(AtTheEnd(list), $"while writing: {Where(list)}");

            await WriteAsync(driving, shell, string.Empty);

            said.Add(Said(32, "the reply"));
            said.Add(Said(33, "the answer to the reply"));
            shell.Transcript.Follow(Conversation, "alpha", Holding(said));
            driving.App.LayoutAndDraw(true);

            Assert.True(AtTheEnd(list), $"after sending: {Where(list)}");
        }
    }

    [Fact]
    public async Task A_reader_halfway_up_who_writes_a_long_reply_is_left_where_they_are()
    {
        // The same reset from the other side: somebody reading back, who starts typing, was thrown to the first message.
        var (driving, directory, shell, said) = await OpenAsync();

        using (driving)
        using (directory)
        using (shell)
        {
            var list = shell.Transcript.List;

            list.Viewport = list.Viewport with { Y = 40 };
            driving.App.LayoutAndDraw(true);

            Assert.Equal(40, list.Viewport.Y);
            Assert.False(shell.Transcript.Tailing, "scrolling up did not count as leaving the end");

            await WriteAsync(driving, shell, LongReply);

            Assert.Equal(40, list.Viewport.Y);

            said.Add(Said(31, "something said meanwhile"));
            shell.Transcript.Follow(Conversation, "alpha", Holding(said));
            driving.App.LayoutAndDraw(true);

            Assert.Equal(40, list.Viewport.Y);
        }
    }
}
