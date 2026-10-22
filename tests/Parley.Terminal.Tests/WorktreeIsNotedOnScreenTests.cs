using Parley.Setup;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the terminal's enrolment picker says a repository is a worktree (T055, the surface half).
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by trying to write the command line's half of T055.</b> `EnrollCommand` had worked the
/// note out privately for its own picker — "worktree of another checkout", "already an agent", "has
/// an .mcp.json" — and this surface composed its own from <c>Owns</c> alone, under a remark saying
/// it was "read off the row the command produced ... so the note and what the command will do
/// cannot disagree". They disagreed: on the surface the owner actually enrols from, a worktree was
/// offered with nothing said about it.
/// </para>
/// <para>
/// <b>Why a worktree is worth a line.</b> It is the same repository at another path, and a folder of
/// worktrees looks exactly like a folder of checkouts to a scan — so enrolling one gives a second
/// agent to one codebase, and because <c>.mcp.json</c> is a tracked file the two agents' launch
/// configurations are the same path on two branches. Whichever branch is checked out decides which
/// agent exists.
/// </para>
/// <para>
/// The note is on <see cref="EnrollmentEntry.Note"/> now, so both pickers read one value and this is
/// the check that this surface shows it.
/// </para>
/// </remarks>
public sealed class WorktreeIsNotedOnScreenTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-worktree-screen-" + Path.GetRandomFileName());

    public WorktreeIsNotedOnScreenTests() => Directory.CreateDirectory(Checkouts);

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

    [Fact]
    public void The_row_a_person_reads_says_worktree()
    {
        // A repository and a worktree of it, exactly as a folder of both would look to the scan.
        var main = Path.Combine(Checkouts, "Acme.Orders");

        Directory.CreateDirectory(Path.Combine(main, ".git"));

        var worktree = Path.Combine(Checkouts, "Acme.Orders.fix");

        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /elsewhere/.git/worktrees/fix\n");

        var found = EnrollCommand.Find(Checkouts);

        // **Asserted on the dialog's own rendered row, not on the entry.** The entry carrying the
        // note is necessary and not sufficient: the defect was a surface that had the value
        // available and composed something else.
        using var session = Driving.Open();

        var dialog = new TickDialog(
            "Enrolment",
            "Tick the ones that should become agents.",
            // **Through `Onboarding.Note`, not `entry.Note`.** A test that read the row's own note
            // and put it on the screen itself would pass whatever this surface composed — which is
            // exactly how the defect survived: the value was there and the surface ignored it.
            [.. found.Select(entry => new Tickable(
                Path.GetFileName(entry.Workspace.TrimEnd('/')),
                Onboarding.Note(entry),
                Ticked: false))]);

        session.Show(dialog);

        var screen = session.Text();

        Assert.Contains("worktree", screen, StringComparison.OrdinalIgnoreCase);

        // And the ordinary repository beside it carries no such note, so the line means something.
        Assert.Single(
            found,
            entry => entry.Note.Contains("worktree", StringComparison.OrdinalIgnoreCase));
    }
}
