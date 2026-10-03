using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a line sent on coming back from being away goes where the conversation is now, not where it was (specs/023).
/// </summary>
/// <remarks>
/// <para>
/// The combined review gate's finding (lens 1, run 2). While a person is away the follow reads the conversation and leaves
/// the draft alone, since with no stream there is nothing to derive "owed by you" from. 0.36.1 made Enter while away take
/// the stream back and send — and it sent that old draft. A Request that arrived meanwhile was answered with a new Request
/// on a new exchange, the agent's question left open and the person shown as owing it, under "Sent to 1 of 1".
/// </para>
/// <para>
/// Run both ways: never away is the control, and must pass against the old code too.
/// </para>
/// </remarks>
public sealed class BackFromAwayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_request_that_arrived_while_away_is_answered_by_the_line_sent_on_return(bool away)
    {
        await using var seated = await Seated.OpenAsync(Ct);
        var shell = seated.Shell;
        var project = new ProjectSettings(Walking.Alpha, "unused.db", seated.Address, null);

        // Answer the fixture's request, so nothing is open before the new one.
        shell.Line.Written = "yes, it is green";
        shell.Line.Accept();
        await shell.Settled;

        shell.Line.Written = "the deploy moves to noon";

        if (away)
        {
            shell.Tick(DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(5));
            Assert.Null(shell.Held);
        }

        var asked = await seated.AsksAsync(
            Seated.Orders, seated.Person, "when does the deploy happen?", Seated.Conversation, Ct);

        // The follow tick, which reads the conversation every three seconds.
        await shell.OpenAsync(project, Seated.Conversation, Ct);
        await shell.Settled;

        // Back: Enter is the first key.
        shell.Touched();
        shell.Line.Accept();
        await shell.Settled;

        var export = await seated.Operators.ExportAsync(Seated.Conversation, Ct);
        var sent = Assert.Single(export!.Messages, m => m.PayloadJson.Contains("noon", StringComparison.Ordinal));

        Assert.Equal(asked, sent.ExchangeId);
    }

    [Fact]
    public async Task Somebody_joining_while_away_stops_the_line_rather_than_sending_it_elsewhere()
    {
        // Who a line reaches is the one thing the preview promises, so a changed room is shown before anything is sent.
        await using var seated = await Seated.OpenAsync(Ct);
        var shell = seated.Shell;
        var project = new ProjectSettings(Walking.Alpha, "unused.db", seated.Address, null);

        shell.Line.Written = "the deploy moves to noon";
        shell.Tick(DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(5));

        // The person, on another surface, brings billing in.
        _ = await seated.AsksAsync(seated.Person, Seated.Billing, "can you look too?", Seated.Conversation, Ct);

        await shell.OpenAsync(project, Seated.Conversation, Ct);
        await shell.Settled;

        shell.Touched();
        shell.Line.Accept();
        await shell.Settled;

        var export = await seated.Operators.ExportAsync(Seated.Conversation, Ct);

        Assert.DoesNotContain(export!.Messages, m => m.PayloadJson.Contains("noon", StringComparison.Ordinal));
        Assert.Contains("changed since it was shown", shell.Said, StringComparison.Ordinal);
        Assert.Equal("the deploy moves to noon", shell.Line.Written);
    }
}
