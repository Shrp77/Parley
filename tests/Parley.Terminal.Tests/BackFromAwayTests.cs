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
/// <b>specs/025 reversed what the first test holds.</b> It asserted the line on return answered the Request that arrived
/// while away; since 0.41.0 every line is a new Request and the agent decides what it settled (SPEC.md §5.6), so it
/// asserts that instead. What is left of 0.38.2's finding is the second test: a changed room is shown, not sent to.
/// </para>
/// <para>
/// Run both ways: never away is the control.
/// </para>
/// </remarks>
public sealed class BackFromAwayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_line_sent_on_return_is_a_request_and_what_arrived_while_away_stays_open(bool away)
    {
        await using var seated = await Seated.OpenAsync(Ct);
        var shell = seated.Shell;
        var project = new ProjectSettings(Walking.Alpha, "unused.db", seated.Address, null);

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

        Assert.Equal(MessageType.Request, sent.Type);
        Assert.NotEqual(asked, sent.ExchangeId);
        Assert.Equal(ExchangeState.Open, export.Exchanges.Single(e => e.ExchangeId == asked).State);
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
