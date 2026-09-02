using Parley.Cli.Top;
using Spectre.Console.Testing;
using Xunit;

namespace Parley.Cli.Tests.Top;

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
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"parley-rotate-{Guid.NewGuid():N}");

    private readonly string _path;

    public LogRotationTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "broker.log");
        File.WriteAllText(_path, "already here\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A follower on a worker can still hold the handle when the test ends. The temp
            // directory is the operating system's problem at that point, not this test's.
        }
    }

    [Fact]
    public async Task Lines_appended_after_the_log_is_truncated_are_still_followed()
    {
        // A broker that rotates by truncating leaves the reader parked past the new end of file,
        // where every read returns null forever. The screen keeps saying "Following ..." and
        // shows nothing again, which is indistinguishable from a quiet broker.
        using var console = new TestConsole();
        using var stopping = new CancellationTokenSource();

        var following = LogTail.FollowAsync(console, _path, string.Empty, stopping.Token);

        await AppendAsync("before rotation");
        await UntilAsync(() => console.Output.Contains("before rotation", StringComparison.Ordinal));

        Truncate();

        await AppendAsync("after rotation");

        try
        {
            await UntilAsync(() => console.Output.Contains("after rotation", StringComparison.Ordinal));
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
        using var console = new TestConsole();
        using var stopping = new CancellationTokenSource();

        var following = LogTail.FollowAsync(console, _path, string.Empty, stopping.Token);

        await AppendAsync("something");
        await UntilAsync(() => console.Output.Contains("something", StringComparison.Ordinal));

        await stopping.CancelAsync();

        // Completes, and does not throw: cancelling a follow is the ordinary way out of it.
        await following;

        Assert.True(following.IsCompletedSuccessfully);
    }

    /// <summary>Appends one line, the way a broker writing its log would.</summary>
    /// <param name="line">What to write.</param>
    /// <returns>When it is on disk.</returns>
    private async Task AppendAsync(string line)
    {
        await using var writer = new FileStream(
            _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        await using var text = new StreamWriter(writer);

        await text.WriteLineAsync(line);
    }

    /// <summary>Empties the file in place, the way a rotating writer would.</summary>
    private void Truncate()
    {
        using var file = new FileStream(
            _path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>Waits for the follower to catch up, or fails saying it did not.</summary>
    /// <param name="done">What is being waited for.</param>
    /// <returns>When it has happened.</returns>
    private static async Task UntilAsync(Func<bool> done)
    {
        var giveUp = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (!done())
        {
            Assert.True(DateTimeOffset.UtcNow < giveUp, "the follower never showed it");

            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        }
    }
}
