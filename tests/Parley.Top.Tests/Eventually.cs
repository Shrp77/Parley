using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// Waiting for something a background worker is going to do.
/// </summary>
/// <remarks>
/// <para>
/// Polled rather than slept for a fixed span. The things waited on here are a follow loop's 200ms
/// tick and a seat's two-second reattach, so a sleep long enough to be reliable on a loaded runner
/// would be seconds spent on every green run.
/// </para>
/// <para>
/// One copy, because there were three within this assembly alone and five across the suite — which
/// is how one of them quietly grows a longer budget than the others and starts hiding a real
/// timing bug behind a green tick.
/// </para>
/// </remarks>
internal static class Eventually
{
    /// <summary>How long to allow before calling it a failure.</summary>
    /// <remarks>
    /// Generous on purpose. This bounds a hang; it is not a performance assertion, and a tight
    /// budget here buys nothing but a flake on a busy machine.
    /// </remarks>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>Waits for a condition, or fails the test saying it never held.</summary>
    /// <param name="done">What is being waited for.</param>
    /// <param name="because">What should have happened, said the way a failure should read.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>When it has happened.</returns>
    internal static async Task UntilAsync(
        Func<bool> done, string because, CancellationToken cancellationToken = default)
    {
        var giveUp = DateTimeOffset.UtcNow + Budget;

        while (!done())
        {
            Assert.True(DateTimeOffset.UtcNow < giveUp, because);

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }
}
