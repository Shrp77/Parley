using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a person who comes back to a conversation after the idle window can send in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's report of 0.36.0</b>: they let the agents in a project work out a production plan, came back,
/// typed a question into the conversation and pressed Enter, and were told <i>"Your seat is held, but this view resolves
/// no project to speak into … That should not be possible"</i>. The project was fine. What was missing was the draft.
/// </para>
/// <para>
/// Going idle drops the stream, which set <c>Held</c> to null, and that setter also threw the draft away — right while
/// the seat went with the stream, and wrong now, where only the connection goes. The next key took the stream
/// back and nothing rebuilt the draft, so the send found a seat and no draft, and the guard that folds three absences
/// into one blamed the project.
/// </para>
/// </remarks>
[Integration]
public sealed class SendAfterIdleTests
{
    [Fact]
    public async Task Coming_back_after_the_idle_window_and_pressing_Enter_sends()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);
        var shell = seated.Shell;

        var later = DateTimeOffset.UtcNow + Parley.Client.Seating.SeatBook.IdleAfter + TimeSpan.FromMinutes(5);

        // Nobody at the terminal while the agents work; then a key, as typing the question is.
        shell.Tick(later);
        Assert.Null(shell.Held);

        shell.Touched(later);
        await shell.Settled;
        Assert.NotNull(shell.Held);

        shell.Line.Written = "Can you agree on one agent that has the big picture?";
        shell.Line.Accept();
        await shell.Settled;

        Assert.NotEqual(Shell.SeatWithoutProject, shell.Said);

        var export = await seated.Operators.ExportAsync(Seated.Conversation, ct);

        Assert.Contains(
            export?.Messages ?? [],
            m => m.PayloadJson.Contains("big picture", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pressing_Enter_while_away_takes_the_stream_back_and_sends()
    {
        // The other order: Enter is the first key after the window. A key is a key, so this must send too rather than
        // say "not connected" about a seat the broker never took away.
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);
        var shell = seated.Shell;

        shell.Line.Written = "Which of you owns the release checklist?";

        shell.Tick(DateTimeOffset.UtcNow + Parley.Client.Seating.SeatBook.IdleAfter + TimeSpan.FromMinutes(5));
        Assert.Null(shell.Held);

        shell.Line.Accept();
        await shell.Settled;

        var export = await seated.Operators.ExportAsync(Seated.Conversation, ct);

        Assert.Contains(
            export?.Messages ?? [],
            m => m.PayloadJson.Contains("release checklist", StringComparison.Ordinal));
    }
}
