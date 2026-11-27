using Parley.Client.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That opening a conversation, which takes delivery, leaves the waiting count on the status line as it was.
/// </summary>
/// <remarks>
/// The count is what waits on the person's answer, never the mail not yet collected (owner, 2026-11-06): a question is
/// still waiting after its message has been collected. Collecting on arrival once set the count to none, so a person who
/// opened a conversation saw nothing waiting until the next ask, with the question still unanswered. Review, lens 3 run 1
/// on 6fde4ef3: putting that back passed every test. The count is asked for every three seconds here, far longer than the
/// open takes, so the count read after it is the one the open left.
/// </remarks>
[Integration]
public sealed class OpeningAConversationKeepsTheCountTests
{
    [Fact]
    public async Task Opening_a_conversation_with_a_question_waiting_leaves_the_count_on_the_status_line()
    {
        var ct = TestContext.Current.CancellationToken;

        // The fixture's own question: orders asked the person something in its conversation.
        await using var seated = await Seated.OpenAsync(ct, mailEvery: TimeSpan.FromSeconds(3));

        await Eventually.UntilAsync(() => seated.Shell.Mail is { Certain: true, Waiting: 1 }, "the question was never counted", ct);

        await Walking.GoAsync(seated.Shell, Viewing.Conversation, Seated.Conversation);
        await seated.Shell.Settled;

        // Opened, so delivery was taken, and the question still waits on the person's answer.
        Assert.Equal((1, true), seated.Shell.Mail);

        seated.Lay();

        Assert.Contains("1 waiting", seated.Session.Text(), StringComparison.Ordinal);
    }
}
