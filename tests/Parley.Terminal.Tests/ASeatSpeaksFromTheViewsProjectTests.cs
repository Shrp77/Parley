using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a held seat can speak from whatever view resolves a project, not only from the list's selection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by reading rather than by use, and the reading is recorded.</b> <c>Shell.Voice()</c> and
/// <c>Shell.Tick()</c> both asked <c>Projects.Selected</c> which project to speak into.
/// <see cref="Shell"/> has <c>Current</c> for exactly this, and its own remark says why: <i>"They are
/// the same until the projects list is off screen, which is exactly when every action inside a project
/// runs — so reading the list made those actions depend on a region nobody is looking at, and on a
/// region that may never have been filled at all."</i>
/// </para>
/// <para>
/// <b>It was latent, not live, and the difference is stated rather than glossed.</b> The one route a
/// person has into a project view is selecting a row and pressing Enter, so the list is always filled
/// first. The guarantee rested on navigation rather than on the seat — and CLAUDE.md records this exact
/// shape appearing four times already, each time because something new reached a view by a route
/// nobody had considered. 021's menu bar is the next thing that could.
/// </para>
/// <para>
/// <b>What it would have cost is a sentence that is provably false.</b> Every send checks
/// <c>Voice()</c> and, finding nothing, said <c>Released</c> — <i>"Your seat was released"</i> — to
/// somebody whose seat is right there. 0.29.1 records that same sentence being the defect: a console
/// told somebody they were seated nowhere while two Results waited for the name whose seat they held.
/// </para>
/// </remarks>
public sealed class ASeatSpeaksFromTheViewsProjectTests
{
    [Fact]
    public async Task A_held_seat_still_speaks_when_the_projects_list_holds_nothing()
    {
        // **The probe.** The seat is held and the view names its project; only the list is empty, which
        // is the state any route into a project view that did not go through the list would produce.
        //
        // MUST FAIL against `Voice()` reading `Projects.Selected`: with the list emptied it returns
        // null, and the surface then reports the seat as released.
        var ct = TestContext.Current.CancellationToken;
        await using var seated = await Seated.OpenAsync(ct);

        Assert.NotNull(seated.Shell.Held);
        Assert.NotNull(seated.Shell.Voice());

        // Empty the list without leaving the view. `Show` with no projects is what a failed manifest
        // read does, so this is a state the surface already produces for its own reasons.
        seated.Shell.Projects.Show([], new Dictionary<string, Parley.Client.ProjectHealth>(), null);

        Assert.Null(seated.Shell.Projects.Selected);
        Assert.NotNull(seated.Shell.Held);
        Assert.NotNull(seated.Shell.Voice());
    }

    [Fact]
    public async Task A_held_seat_is_still_touched_when_the_projects_list_holds_nothing()
    {
        // `Tick` is what keeps a seat alive, and it read the same wrong accessor. With an empty list it
        // returned before touching anything -- so the seat was neither kept nor released, which is
        // worse than unreadable: frozen, and then reported as gone by whatever spoke next.
        var ct = TestContext.Current.CancellationToken;
        await using var seated = await Seated.OpenAsync(ct);

        seated.Shell.Projects.Show([], new Dictionary<string, Parley.Client.ProjectHealth>(), null);

        var before = seated.Shell.Held;

        Assert.NotNull(before);

        seated.Shell.Tick(DateTimeOffset.UtcNow);

        Assert.NotNull(seated.Shell.Held);

        // What matters is that the tick did not decide the seat had gone. Since specs/023 no tick says so at all, so the
        // sentences it could have produced are the not-connected one and the no-project one.
        Assert.NotEqual(Shell.NotConnected, seated.Shell.Said);
        Assert.NotEqual(Shell.SeatWithoutProject, seated.Shell.Said);
    }

    [Fact]
    public void The_warning_says_the_seat_is_held_and_what_to_do()
    {
        // If the state ever happens anyway, the surface says so instead of blaming the seat. Asserted
        // because the whole point of the message is what it does NOT claim.
        Assert.Contains("seat is held", Shell.SeatWithoutProject, StringComparison.Ordinal);
        Assert.Contains("Escape", Shell.SeatWithoutProject, StringComparison.Ordinal);
        Assert.Contains("defect worth reporting", Shell.SeatWithoutProject, StringComparison.Ordinal);

        // It must not be the sentence that was wrong.
        Assert.DoesNotContain("was released", Shell.SeatWithoutProject, StringComparison.Ordinal);
    }
}
