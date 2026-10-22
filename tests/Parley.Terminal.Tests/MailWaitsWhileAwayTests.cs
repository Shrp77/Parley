using Parley.Client.Seating;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That what is waiting for the person is counted while they are away, not only while a stream is held.
/// </summary>
/// <remarks>
/// The terminal stopped asking, and cleared the count, the moment the idle sweep took the stream — so somebody coming back
/// to the screen saw nothing about what had arrived while they were gone, which is exactly when the count matters. The
/// seat is the broker's and its mail waits regardless; the count is asked for without a stream, so asking does not make
/// the person present.
/// </remarks>
public sealed class MailWaitsWhileAwayTests
{
    [Fact]
    public async Task What_arrives_while_the_person_is_away_is_counted()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct, mailEvery: TimeSpan.FromMilliseconds(50));
        var shell = seated.Shell;

        shell.Tick(DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(5));
        Assert.Null(shell.Held);

        _ = await seated.AsksAsync(Seated.Billing, seated.Person, "are you there?", "elsewhere", ct);

        // The fixture's own request was taken when it opened its conversation, so one is waiting.
        for (var tries = 0; tries < 100 && shell.Mail is not { Waiting: >= 1 }; tries++)
        {
            await Task.Delay(50, ct);
            await shell.Settled;
        }

        Assert.Null(shell.Held);
        Assert.Equal((1, true), shell.Mail);
    }
}
