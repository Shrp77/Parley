using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That what is typed after Enter is neither sent nor lost.
/// </summary>
/// <remarks>
/// The combined review gate's finding (lens 1, run 4). The send runs when the work queue reaches it — behind a follow read,
/// a mail poll or a slow broker — and the draft's text was set on every key meanwhile, so the person's next thought went
/// out in the payload of an answer and was then cleared from the box. Held here by putting work ahead of the send, as
/// the queue does in use.
/// </remarks>
public sealed class TypingAfterEnterTests
{
    private const string Next = " -- and then I was going to say something else";

    [Fact]
    public async Task Text_typed_after_Enter_is_not_sent_even_when_a_follow_runs_first()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct, followEvery: TimeSpan.FromMilliseconds(50));
        var shell = seated.Shell;

        var gate = new TaskCompletionSource();
        shell.Queued(_ => gate.Task);

        // Let a follow tick queue its read behind it, as it does every three seconds.
        await Task.Delay(400, ct);

        shell.Line.Written = "yes";
        shell.Line.Accept();
        shell.Line.Written = "yes" + Next;

        gate.SetResult();

        for (var i = 0; i < 20; i++)
        {
            await shell.Settled;
            await Task.Delay(50, ct);
        }

        shell.FollowEvery = null;
        await shell.Settled;

        var export = await seated.Operators.ExportAsync(Seated.Conversation, ct);

        Assert.Contains(export!.Messages, m => m.PayloadJson.Contains("\"yes\"", StringComparison.Ordinal));
        Assert.DoesNotContain(export.Messages, m => m.PayloadJson.Contains("something else", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Text_typed_after_Enter_stays_in_the_box()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);
        var shell = seated.Shell;

        var gate = new TaskCompletionSource();
        shell.Queued(_ => gate.Task);

        shell.Line.Written = "yes";
        shell.Line.Accept();
        shell.Line.Written = "yes" + Next;

        gate.SetResult();
        await shell.Settled;
        await shell.Settled;

        Assert.Equal(Next.TrimStart(), shell.Line.Written);
    }
}
