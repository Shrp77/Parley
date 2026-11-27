using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a git worktree is reported as one, rather than silently enrolled.
/// </summary>
/// <remarks>
/// <para>
/// <b>A worktree is the same repository at another path</b>, and a folder of worktrees looks exactly
/// like a folder of checkouts to a scan. Enrolling one gives a second agent to one codebase — and
/// <c>.mcp.json</c> is a tracked file, so the two agents' launch configurations are the same path in
/// one repository on two branches. Whichever branch is checked out decides which agent's
/// configuration exists.
/// </para>
/// <para>
/// <b>The rule is in the code and this checks it.</b> `EnrollCommand.Note` says "worktree
/// of another checkout", and the terminal's browser counts one as a repository, which
/// it must, because a person may genuinely want to walk into one. That second fact is not the property
/// checked here.
/// </para>
/// <para>
/// <b>Reported rather than refused, deliberately.</b> Two agents on one codebase is a thing somebody
/// may mean — a worktree per branch with an agent on each is a reasonable arrangement — so this is a
/// note beside the row and not a rule. What must not happen is it going unsaid, because the cost
/// only shows up later, as a launch configuration that changes under a branch switch.
/// </para>
/// </remarks>
[Integration]
public sealed class WorktreeIsNotASecondAgentTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-worktree-" + Path.GetRandomFileName());

    public WorktreeIsNotASecondAgentTests() => Directory.CreateDirectory(Checkouts);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Checkouts => Path.Combine(_root, "checkouts");

    /// <summary>A repository: its <c>.git</c> is a directory.</summary>
    private string Repository(string name)
    {
        var path = Path.Combine(Checkouts, name);

        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    /// <summary>A worktree: its <c>.git</c> is a *file* holding <c>gitdir:</c>.</summary>
    /// <remarks>
    /// That is the whole of the distinction, and it needs no git on PATH — which is why the
    /// production check is a file-versus-directory test rather than a subprocess.
    /// </remarks>
    private string Worktree(string name, string of)
    {
        var path = Path.Combine(Checkouts, name);

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".git"), $"gitdir: {of}/.git/worktrees/{name}\n");

        return path;
    }

    [Fact]
    public void A_worktree_is_found_and_marked_as_one()
    {
        var main = Repository("Acme.Orders");
        _ = Worktree("Acme.Orders.fix", main);

        var found = EnrollCommand.Find(Checkouts);

        // Both are offered: a worktree is a git checkout and a person may mean to walk into one.
        Assert.Equal(2, found.Count);

        var worktree = found.Single(row => row.Workspace.EndsWith("Acme.Orders.fix", StringComparison.Ordinal));

        Assert.Contains("worktree", worktree.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_repository_beside_it_is_not_marked()
    {
        // The other half, and the one that makes the first mean something: a note on every row is a
        // note on none. `Acme.Orders` here has a `.git` directory and no `.mcp.json`, so it has
        // nothing to say.
        var main = Repository("Acme.Orders");
        _ = Worktree("Acme.Orders.fix", main);

        var found = EnrollCommand.Find(Checkouts);
        var repository = found.Single(row => row.Workspace.EndsWith("Acme.Orders", StringComparison.Ordinal));

        Assert.DoesNotContain("worktree", repository.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nothing_is_ticked_by_the_scan()
    {
        // **The half that keeps the note honest.** A scan that marked a worktree and then offered it
        // pre-ticked would be describing a decision it had already taken. `Enroll` is false on
        // everything a scan produces — the same property `ProbeTwentyThreeTests` holds for consent,
        // which is the other field a repository's own content must never set.
        var main = Repository("Acme.Orders");
        _ = Worktree("Acme.Orders.fix", main);

        Assert.DoesNotContain(EnrollCommand.Find(Checkouts), row => row.Enroll);
    }

    [Fact]
    public void A_worktree_whose_marker_is_a_directory_is_an_ordinary_repository()
    {
        // The inverse, so the check cannot pass by calling everything a worktree. A `.git`
        // directory is a repository whatever the folder is named.
        _ = Repository("Acme.Orders.fix");

        var found = EnrollCommand.Find(Checkouts);

        Assert.DoesNotContain(found, row => row.Note.Contains("worktree", StringComparison.OrdinalIgnoreCase));
    }
}
