using Parley.Cli.Top;
using Parley.Core;
using Spectre.Console.Testing;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That following a log does not stop the broker writing it.
/// </summary>
/// <remarks>
/// <para>
/// Research R4's claim, probed rather than restated. A reader holding a file with the default
/// share mode does not merely risk a stale read: it <b>stops the writer</b>. That was measured in
/// this repository on the manifest — 287 of 300 renames denied against readers holding
/// <c>FileShare.Read</c>, none against readers holding <c>ReadWrite | Delete</c>. A tail is a
/// reader that stays open, which is the worst version of it.
/// </para>
/// <para>
/// So the test does the thing that would fail: it holds the tail open and renames the file
/// underneath it, which is what a rotation is. If the share mode were wrong the rename would be
/// refused, and a broker rotating its log would find the operator's own log viewer holding it
/// shut.
/// </para>
/// </remarks>
public sealed class LogTailTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"parley-log-{Guid.NewGuid():N}");

    public LogTailTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the run is untidy and not a failure.
        }
    }

    [Fact]
    public async Task A_tail_does_not_stop_the_log_being_rotated()
    {
        var path = Path.Combine(_directory, LogTail.FileName);

        await File.WriteAllTextAsync(
            path, "started\n", TestContext.Current.CancellationToken);

        using var console = new TestConsole();
        using var stopping = new CancellationTokenSource();

        var following = LogTail.FollowAsync(console, path, string.Empty, stopping.Token);

        // The rotation. This is the operation that fails outright against a reader holding the
        // file without FileShare.Delete, and the whole reason the share mode is what it is.
        var rotated = Path.Combine(_directory, LogTail.FileName + ".1");

        File.Move(path, rotated);

        Assert.True(File.Exists(rotated), "the log could not be rotated while it was followed");

        await stopping.CancelAsync();
        await following;
    }

    [Fact]
    public async Task A_tail_does_not_stop_the_log_being_appended_to()
    {
        var path = Path.Combine(_directory, LogTail.FileName);

        await File.WriteAllTextAsync(
            path, "started\n", TestContext.Current.CancellationToken);

        using var console = new TestConsole();
        using var stopping = new CancellationTokenSource();

        var following = LogTail.FollowAsync(console, path, string.Empty, stopping.Token);

        // Opened the way the broker opens it. A reader holding the default share mode makes this
        // throw, which is a broker that cannot write its own log because somebody is reading it.
        await using (var writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)))
        {
            await writer.WriteLineAsync("a line the broker wrote while it was watched");
        }

        await stopping.CancelAsync();
        await following;
    }

    [Fact]
    public void A_log_is_looked_for_beside_the_store()
    {
        // Derived rather than configured, because two places naming one file is how they come to
        // name different ones. AutostartPlan passes exactly this to --log.
        var project = new ProjectSettings(
            ProjectName.Create("alpha"),
            Path.Combine(_directory, "parley.db"),
            "http://127.0.0.1:5301",
            null,
            null);

        var found = LogTail.For(project);

        Assert.NotNull(found);
        Assert.Equal(Path.Combine(_directory, LogTail.FileName), found);
    }

    [Fact]
    public void A_project_with_no_store_has_no_log_to_guess_at()
    {
        var project = new ProjectSettings(
            ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5301", null, null);

        Assert.Null(LogTail.For(project));
    }
}
