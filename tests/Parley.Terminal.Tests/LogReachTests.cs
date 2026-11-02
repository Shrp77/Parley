using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the log view reads the file the deployment writes, and cannot be pointed at another.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.5. This is the one thing on the surface read from disk rather
/// than fetched over the transport, so it is the one place a path is resolved at all — and the two
/// inputs it resolves from are a manifest somebody may not have written and an autostart entry
/// read from the machine's scheduler.
/// </para>
/// <para>
/// What it must not become is a file reader. The filter is matched against lines and never against
/// a path, and there is no field anywhere that names a file to follow: the path is derived, which
/// is what stops "show me the log" turning into "show me any file this process can open".
/// </para>
/// </remarks>
[Integration]
public sealed class LogReachTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-logreach-" + Path.GetRandomFileName());

    public LogReachTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    [Fact]
    public void The_log_is_always_the_broker_s_own_file_name()
    {
        // Derived rather than configured: two places naming one file is how they come to name
        // different ones, and this is the name AutostartPlan passes to --log.
        var project = Project(Path.Combine(_root, "alpha.db"));

        Assert.EndsWith(LogTail.FileName, LogTail.For(project, null)!, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_beside_the_store_and_not_somewhere_a_manifest_asked_for()
    {
        // The manifest names a store and an endpoint. It does not name a log, and a project that
        // could would be a file path taken from a file.
        var store = Path.Combine(_root, "nested", "alpha.db");
        var project = Project(store);

        var log = LogTail.For(project, null)!;

        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(store)), Path.GetDirectoryName(log));
    }

    [Fact]
    public void A_project_with_no_store_has_no_log_to_guess_at()
    {
        // Rather than falling back to a working directory, which would follow whatever file
        // happened to be beside the process.
        Assert.Null(LogTail.For(Project(string.Empty), null));
    }

    [Fact]
    public void A_store_that_is_not_a_path_reports_no_log_rather_than_throwing()
    {
        // A manifest is a file, and a file can hold anything. What it must not do is take the
        // surface down on the way to its one convenience view.
        //
        // A null character, built from its code rather than typed -- 004 recorded why, and the
        // reason survived this rewrite: it is invalid in a path on every platform this runs on,
        // which a Windows-only invalid character such as a pipe is not, and a pipe would pass here
        // and fail on two of the three runners. A control byte typed into a source file is also
        // invisible in most editors, which this repository has had happen more than once.
        var unusable = "a" + ((char)0) + "b";

        Assert.Null(LogTail.For(Project(unusable), null));
        Assert.Null(LogTail.For(Project("   "), null));
    }

    [Fact]
    public async Task The_filter_selects_lines_and_never_a_file()
    {
        // The one thing a person types on this screen. It is matched against the contents of the
        // file already chosen -- a filter that reached the path would be an open-any-file field
        // wearing a search box.
        var path = Path.Combine(_root, LogTail.FileName);
        var elsewhere = Path.Combine(_root, "secrets.txt");

        await File.WriteAllTextAsync(path, string.Empty, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(elsewhere, "not for reading\n", TestContext.Current.CancellationToken);

        var collected = new Collected();

        using var stopping = new CancellationTokenSource();

        var following = LogTail.FollowAsync(collected.Write, path, elsewhere, stopping.Token);

        await File.AppendAllTextAsync(
            path, "a line mentioning " + elsewhere + "\n", TestContext.Current.CancellationToken);

        await Eventually.UntilAsync(
            () => collected.Lines.Count > 0,
            "the follower never read the line it was given",
            TestContext.Current.CancellationToken);

        await stopping.CancelAsync();
        await following;

        Assert.DoesNotContain("not for reading", collected.Output, StringComparison.Ordinal);
    }

    private static ProjectSettings Project(string store) =>
        new(ProjectName.Create("alpha"), store, "http://127.0.0.1:1", null);
}
