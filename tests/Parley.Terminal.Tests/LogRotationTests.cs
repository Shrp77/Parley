using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// What the log follower does when the file moves under it.
/// </summary>
/// <remarks>
/// The follower opens with <c>FileShare.ReadWrite | FileShare.Delete</c> precisely so the broker
/// can keep appending and can rotate the file out from under it — the share flags say the case was
/// anticipated. What nothing had checked is what the reader does *afterwards*, which is a different
/// question from whether the broker is allowed to do it.
/// </remarks>
public sealed class LogRotationTests : IDisposable
{
    private readonly LogFile _log = new("rotate");

    public void Dispose() => _log.Dispose();

    [Fact]
    public async Task Lines_appended_after_the_log_is_truncated_are_still_followed()
    {
        // A broker that rotates by truncating leaves the reader parked past the new end of file,
        // where every read returns null forever. The screen keeps saying "Following ..." and
        // shows nothing again, which is indistinguishable from a quiet broker.
        var token = TestContext.Current.CancellationToken;
        var console = new Collected();
        using var stopping = new CancellationTokenSource();

        var following = LogTail.FollowAsync(console.Write, _log.At, string.Empty, stopping.Token);

        try
        {
            await _log.AppendAsync("before rotation");
            await Eventually.UntilAsync(
                () => console.Output.Contains("before rotation", StringComparison.Ordinal),
                "the follower never showed a line written before the rotation",
                token);

            _log.Truncate();

            await _log.AppendAsync("after rotation");

            await Eventually.UntilAsync(
                () => console.Output.Contains("after rotation", StringComparison.Ordinal),
                "the follower stopped following once the log was truncated",
                token);
        }
        finally
        {
            await stopping.CancelAsync();
            await following;
        }
    }

    [Fact]
    public async Task Stopping_the_follow_leaves_no_unobserved_fault()
    {
        // The shape that made two probes in this repository hang for ten minutes. Cancelling has
        // to end the task rather than fault it on a background thread nobody is awaiting.
        var token = TestContext.Current.CancellationToken;
        var console = new Collected();
        using var stopping = new CancellationTokenSource();

        var following = LogTail.FollowAsync(console.Write, _log.At, string.Empty, stopping.Token);

        await _log.AppendAsync("something");
        await Eventually.UntilAsync(
            () => console.Output.Contains("something", StringComparison.Ordinal),
            "the follower never showed the appended line",
            token);

        await stopping.CancelAsync();

        // Completes, and does not throw: cancelling a follow is the ordinary way out of it.
        await following;

        Assert.True(following.IsCompletedSuccessfully);
    }
}
