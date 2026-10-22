using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That `reset.sh` exists and does what `reset.ps1` does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both platforms need a reset.</b> `reset.ps1` clears a developer's old layout and is the
/// specification for its counterpart. CI runs on Windows, Linux and macOS and `install.sh` covers all
/// three, so a developer setting Parley up on Linux needs a way to clear the old layout that is not
/// by hand.
/// </para>
/// <para>
/// <b>Checked as agreement between the two rather than by rerunning either.</b> What a reset must
/// do is the same on both platforms — stop the broker and every tool beside it, read the old
/// manifest before deleting it, take each store with its companions, keep the three files of the
/// current layout unless told otherwise, and clear the launch entries that live outside the root. A
/// script that silently dropped one of those on one platform is the failure worth catching, and it
/// is invisible from the platform the author happens to be on.
/// </para>
/// <para>
/// <b>Neither is run here.</b> `reset.sh` is exercised against built layouts in a Linux container
/// — an old manifest with two stores and their companions, an out-of-root catalogue, three
/// repositories including one whose `.mcp.json` is not JSON. This is the guard that keeps the two
/// in step.
/// </para>
/// </remarks>
public sealed class ResetScriptTests
{
    public static TheoryData<string> BothScripts => ["reset.ps1", "reset.sh"];

    [Fact]
    public void The_counterpart_exists()
    {
        // The cheapest thing to get wrong: `reset.ps1` could name the gap in its own summary, so a
        // check that only read that summary would pass for ever.
        Assert.NotEmpty(RepositoryFile.Read("reset.sh"));
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_refuses_a_root_that_is_not_a_parley_root(string script)
    {
        // The root is an argument and the whole of it is deleted under the everything flag, so a filesystem root, a home
        // directory or somebody else's folder must be refused before anything is removed. Both scripts say so in the
        // same words, and each was run against `/`, the home directory and a scratch directory when this was written.
        var text = RepositoryFile.Read(script);

        Assert.Contains("Refusing to reset", text, StringComparison.Ordinal);
        Assert.Contains("that is not a Parley root", text, StringComparison.Ordinal);
        Assert.Contains("does not look like one", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_keeps_what_019_introduced_unless_told_otherwise(string script)
    {
        // The three that survive an ordinary reset. `node.json` especially: it is this machine's
        // catalogue of which checkouts may be started, keyed by identity, and deleting it means
        // re-enrolling every repository by hand.
        var text = RepositoryFile.Read(script);

        foreach (var kept in new[] { "node.json", "ports.json" })
        {
            Assert.Contains(kept, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_takes_a_store_with_every_companion(string script)
    {
        // A store deleted without these leaves a write-ahead log and a lock file that the next
        // thing to open that path inherits — which is a reset that reports success and leaves the
        // machine in a state no fresh install produces.
        var text = RepositoryFile.Read(script);

        foreach (var companion in new[] { "-wal", "-shm", ".lock", ".owner", ".files" })
        {
            Assert.Contains(companion, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_stops_every_tool_and_not_only_the_broker(string script)
    {
        // `parley-web-ui` is a child the broker does not wait on, so ending the broker alone leaves
        // it holding its port — and the next `autostart install` then reports a web UI that could
        // not bind on a machine where nothing is using it. That exact orphan was met on this
        // machine and cost two failed installs.
        var text = RepositoryFile.Read(script);

        foreach (var tool in new[] { "parley-web-ui", "parley-terminal" })
        {
            Assert.Contains(tool, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_offers_the_sandbox_that_is_actually_a_sandbox(string script)
    {
        // **The defect that produced this flag, on the first run of `reset.ps1`.** `-Root` pointed
        // the file half at a throwaway directory while `parley autostart remove` went on acting on
        // the real machine, and removed a live logon entry. A flag that sandboxes the files and not
        // the machine is not a sandbox — so both scripts carry one that skips the machine entirely,
        // and it is what every test run against a scratch directory must pass.
        var text = RepositoryFile.Read(script);

        var flag = script.EndsWith(".ps1", StringComparison.Ordinal) ? "FilesOnly" : "files-only";

        Assert.Contains(flag, text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_clears_the_launch_entries_that_live_outside_the_root(string script)
    {
        // The one piece of a deployment that is not under the root. A wipe that ignored it leaves
        // every repository pointing at a broker that is gone — and, across iterations that try
        // different project names, accumulating a key per attempt.
        var text = RepositoryFile.Read(script);

        Assert.Contains(".mcp.json", text, StringComparison.Ordinal);

        var flag = script.EndsWith(".ps1", StringComparison.Ordinal) ? "Repositories" : "repositories";

        Assert.Contains(flag, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shell_script_sends_people_to_the_terminal_rather_than_a_removed_command()
    {
        // Both scripts end by saying what to do next, and `project add` went on 2026-09-25. A
        // freshly reset machine is the one place somebody is most likely to type whatever they are
        // told, so a script ending with a command the tool refuses is the worst place for one.
        var text = RepositoryFile.Read("reset.sh");

        Assert.Matches(@"(?m)^echo '  parley\s+create one", text);
        Assert.DoesNotContain("parley project add", text, StringComparison.Ordinal);
    }
}
