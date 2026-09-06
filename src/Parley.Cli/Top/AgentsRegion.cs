using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Headless;
using Terminal.Gui.Views;

namespace Parley.Cli.Top;

/// <summary>
/// Who is in the selected project: what its broker's registry holds, and what its catalogue says
/// this machine could start.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two sources, one list, and every difference between them said in words.</b> The registry
/// holds agents that have registered themselves (SPEC.md §12.3) and the catalogue holds what a
/// node may start; they are not the same set and the places they differ are the interesting ones.
/// An agent just added from this surface is in the catalogue and not yet in the registry, and an
/// agent in the registry but not this catalogue is §17 question 13's misleading <c>startable</c>,
/// which used to be visible nowhere.
/// </para>
/// <para>
/// The state of a row present in both comes from <see cref="Roster.State"/> rather than from a
/// word chosen here. Two surfaces deriving presence separately is how they come to disagree about
/// who was available to ask, which SC-005 forbids, and the shared derivation is most of the reason
/// <c>Parley.Client</c> exists.
/// </para>
/// <para>
/// <b>Startable is not absent.</b> An agent nobody is running, on a machine whose node is
/// connected, can be started by that node when a question arrives; an agent whose machine is
/// unreachable cannot. Both look like "not here" and only one of them means a peer cannot be
/// asked, so the words are kept apart.
/// </para>
/// </remarks>
internal sealed class AgentsRegion : FrameView
{
    /// <summary>What this region is called, in the status line and to a test.</summary>
    internal const string RegionTitle = "Agents";

    /// <summary>An agent this machine could start that has never registered itself.</summary>
    /// <remarks>
    /// <b>Kept only so the README's own test can still find what it used to say.</b> 006 replaced
    /// both of these: the owner read them on screen and could not say what either meant, which was
    /// fair, because they name two files a person did not write rather than the facts those files
    /// decide. <see cref="Standing"/> holds the wording now.
    /// </remarks>
    internal const string CataloguedOnly = "catalogued, not yet registered";

    /// <summary>An agent the registry holds that this project's catalogue does not name.</summary>
    internal const string NotCatalogued = "not in this catalogue";

    /// <summary>A person holding a seat rather than an agent.</summary>
    internal const string Seated = "seat";

    /// <summary>What the dialog asking for a seat is called.</summary>
    internal const string JoinTitle = "Take a seat";

    /// <summary>What it says above the field.</summary>
    internal const string JoinQuestion = "Join as:";

    /// <summary>What is said when a seat has been taken.</summary>
    internal const string Took = "Seated as ";

    /// <summary>What is said when the person leaves and was not seated anywhere.</summary>
    private const string NotSeated = "You were not seated anywhere.";

    /// <summary>What is said when a seat is released.</summary>
    internal const string Left = "Left. ";

    /// <summary>What is shown before anything has been read.</summary>
    internal const string Nothing =
        "No agent is registered in this project and its catalogue names none. Press a to make a "
        + "repository on this machine one.";

    private readonly ListView _list = Lists.Filling();

    private List<AgentRow> _rows = [];
    private List<AgentStanding> _standing = [];
    private int _offset;

    internal AgentsRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        Add(_list);
    }

    /// <summary>The rows as drawn, in order.</summary>
    internal IReadOnlyList<AgentRow> Rows => _rows;

    /// <summary>Whichever row the person is on, or null when there are none.</summary>
    /// <remarks>
    /// The offset is why this is not simply an index into the rows. When the broker could not be
    /// asked, the reason occupies the first line and the rows follow it — so a person on the third
    /// line is on the second agent, and a version that forgot that would answer with the agent
    /// above the one highlighted. In this region that is the difference between retiring the right
    /// agent and the wrong one.
    /// </remarks>
    internal AgentRow? Selected =>
        _list.SelectedItem is { } at && at - _offset >= 0 && at - _offset < _rows.Count
            ? _rows[at - _offset]
            : null;

    /// <summary>The list, so a test can pick a row by its text.</summary>
    internal ListView List => _list;

    /// <summary>
    /// Joins a registry to a catalogue, keyed by agent name.
    /// </summary>
    /// <param name="registry">What the broker said, or null when it could not be asked.</param>
    /// <param name="catalogue">What this project's catalogue names.</param>
    /// <returns>One row per name in either, ordered by name.</returns>
    /// <remarks>
    /// A catalogue name that is not a well-formed agent name still gets a row, saying so. Dropping
    /// it would hide a hand-edited file's mistake in the one place somebody would look for it, and
    /// the node will refuse to start it for the same reason.
    /// </remarks>
    internal static IReadOnlyList<AgentRow> Join(
        IReadOnlyList<AgentRecord>? registry,
        NodeCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var rows = new List<AgentRow>();
        var catalogued = new HashSet<string>(catalogue.Roles, StringComparer.Ordinal);

        foreach (var agent in registry ?? [])
        {
            var seat = string.Equals(agent.Adapter, Seat.Adapter, StringComparison.Ordinal);
            var state = seat ? Seated : Roster.State(agent);

            if (!seat && !catalogued.Contains(agent.Name.Value))
            {
                state = $"{state}, {NotCatalogued}";
            }

            // Every value here was written by somebody else: the name and the workspace by the
            // agent's own adapter, the ownership line by whoever wrote its launch configuration.
            // Neutralised as it enters the row rather than as it is drawn, because the row is what
            // the withdrawal dialog quotes back at the person about to purge it.
            rows.Add(new AgentRow(
                Safe.Text(agent.Name.Value),
                state,
                agent.Tier.ToString().ToLowerInvariant(),
                Roster.HasStatedWhatItOwns(agent) ? Safe.Text(agent.Owns) : "not stated",
                Safe.Text(agent.Workspace ?? "—"),
                seat));
        }

        var registered = new HashSet<string>(
            (registry ?? []).Select(a => a.Name.Value), StringComparer.Ordinal);

        foreach (var role in catalogued.Where(role => !registered.Contains(role)))
        {
            var workspace = AgentName.TryCreate(role, out var name)
                ? catalogue.WorkspaceFor(name)
                : null;

            rows.Add(new AgentRow(
                Safe.Text(role),
                CataloguedOnly,
                "—",
                "not stated",
                Safe.Text(workspace ?? "—"),
                Seat: false));
        }

        return [.. rows.OrderBy(row => row.Name, StringComparer.Ordinal)];
    }

    /// <summary>Draws a project's agents.</summary>
    /// <param name="registry">What the broker said, or null when it could not be asked.</param>
    /// <param name="catalogue">What this project's catalogue names.</param>
    /// <param name="problem">Why the broker could not be asked, or null when it could.</param>
    internal void Show(
        IReadOnlyList<AgentRecord>? registry,
        NodeCatalogue catalogue,
        string? problem)
    {
        var keep = Selected?.Name;

        _rows = [.. Join(registry, catalogue)];
        _standing = [.. Standing.Join(
            registry, new HashSet<string>(catalogue.Roles, StringComparer.Ordinal), Seat.Adapter)];

        var lines = _rows.Count > 0
            ? _rows.Select(Line).ToList()
            : [Safe.Text(problem) is { Length: > 0 } why ? why : Nothing];

        if (problem is not null && _rows.Count > 0)
        {
            // Both, rather than one or the other. The catalogue is still true when the broker is
            // not answering, and saying only "not answering" would hide the half of the picture
            // this machine can still see.
            lines.Insert(0, Safe.Text(problem));
        }

        _list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(lines));

        _offset = problem is null || _rows.Count == 0 ? 0 : 1;

        var offset = _offset;
        var at = keep is null ? -1 : _rows.FindIndex(row => row.Name == keep);

        // The selection follows the agent rather than the position. A refresh that moved
        // somebody's highlight onto a different agent is how a destructive action gets taken
        // against the wrong one, and this region is where the destructive actions are.
        _list.SelectedItem = at >= 0 ? at + offset : _rows.Count > 0 ? offset : null;
    }

    /// <summary>
    /// Opens the dialog that seats a person, and seats them if they name themselves acceptably.
    /// </summary>
    /// <param name="seats">The book that holds seats.</param>
    /// <param name="project">The project to sit in.</param>
    /// <param name="registry">What the broker's registry holds, or null when it could not be read.</param>
    /// <param name="typed">The name the person gave.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The seat taken, and what to say about it.</returns>
    /// <remarks>
    /// <para>
    /// A seat is the one thing this surface has that no operator operation grants. §10.2 lets an
    /// operator retire, purge and force-conclude anybody, so a console has never been short of
    /// power — what it could not do is <i>speak as</i> somebody, and a transcript recording words
    /// an agent never said is a different kind of wrong from an operator visibly instructing it.
    /// </para>
    /// <para>
    /// So that is the one thing guarded, and the guard is <c>SeatBook</c>'s rather than a copy of
    /// it: it refuses a name the registry holds for anything whose adapter is not a seat, and
    /// refuses outright when the registry could not be read. A guard that skipped the check on an
    /// unreachable broker would pass hardest exactly when the deployment is least well known,
    /// which is why the registry is passed in rather than shrugged off.
    /// </para>
    /// </remarks>
    internal static async Task<(SeatHold? Hold, string Said)> JoinAsync(
        SeatBook seats,
        ProjectSettings project,
        IReadOnlyList<AgentRecord>? registry,
        string typed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seats);

        if (!Seat.TryRead(typed, out var seat, out var why))
        {
            return (null, why ?? "That is not a name.");
        }

        var outcome = await seats
            .TakeAsync(project, seat, registry, cancellationToken)
            .ConfigureAwait(false);

        return outcome.Seated
            ? (outcome.Hold, Took + seat.Name.Value + ".")
            : (null, outcome.Problem ?? "Not seated.");
    }

    /// <summary>Releases a person's seat in every project at once.</summary>
    /// <param name="seats">The book holding it.</param>
    /// <param name="seat">Whose seat.</param>
    /// <returns>What to say about it.</returns>
    /// <remarks>
    /// Every project, not the one on screen. A person leaves a surface rather than a view, and
    /// releasing only the project they happened to be looking at would leave them present in the
    /// others with nothing anywhere saying so.
    /// </remarks>
    internal static string Leave(SeatBook seats, Seat seat)
    {
        ArgumentNullException.ThrowIfNull(seats);

        var released = seats.Leave(seat);

        return released == 0 ? NotSeated : $"{Left}{released} seat(s) released.";
    }

    /// <summary>
    /// Keeps a seat alive because somebody is looking at the surface, and releases the ones nobody
    /// is.
    /// </summary>
    /// <param name="seats">The book.</param>
    /// <param name="me">The name last seated, if any.</param>
    /// <param name="project">The project on screen.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The seat still held here, or null when there is none.</returns>
    /// <remarks>
    /// <para>
    /// <b>Touch first, then sweep, and the order is the whole of it.</b> Swept first, a person who
    /// spent longer than <see cref="SeatBook.IdleAfter"/> on one screen comes back to find their
    /// seat gone — and the screen they were on is the one where they read a question and write an
    /// answer, which is the slowest and most valuable thing this surface is for. Being unseated
    /// for thinking about the question is the exact opposite of what an idle timeout is for.
    /// </para>
    /// <para>
    /// Sweeping at all is what stops a terminal left open overnight holding somebody present in
    /// the registry, which is the failure <c>IdleAfter</c> exists to prevent — reached by a
    /// different kind of window than the browser tab it was written for. Both halves are needed
    /// and only in this order.
    /// </para>
    /// <para>
    /// The seat is then <i>asked for</i> rather than assumed: whether the name still holds one is
    /// the book's answer, including after the sweep has just taken it. And <paramref name="me"/> is
    /// never overwritten from the result — who somebody is and whether they are seated <i>here</i>
    /// are different questions, and forgetting the first on visiting a project they are not seated
    /// in is a defect this had once already.
    /// </para>
    /// </remarks>
    internal static SeatHold? Refresh(
        SeatBook seats, Seat? me, ProjectSettings project, DateTimeOffset now) =>
        Refresh(seats, me, project, now, now);

    /// <summary>
    /// The same, with the moment somebody last used the surface told apart from the moment now.
    /// </summary>
    /// <param name="seats">The book.</param>
    /// <param name="me">The name last seated, if any.</param>
    /// <param name="project">The project on screen.</param>
    /// <param name="touched">When somebody last pressed a key or clicked.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The seat still held here, or null when there is none.</returns>
    /// <remarks>
    /// <b>The two are not the same instant, and treating them as one is how the idle rule stops
    /// applying.</b> A terminal redraws itself whether or not anybody is at it, so a touch taken at
    /// the moment of drawing means the seat of an abandoned terminal is refreshed for ever — which
    /// is the failure <see cref="SeatBook.IdleAfter"/> exists to prevent, and which this surface
    /// had until a probe went looking for it. What the specification asks for is twenty minutes
    /// <i>without touching it</i>, so the touch is dated from the last key or click.
    /// </remarks>
    internal static SeatHold? Refresh(
        SeatBook seats,
        Seat? me,
        ProjectSettings project,
        DateTimeOffset touched,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(seats);
        ArgumentNullException.ThrowIfNull(project);

        if (me is not null)
        {
            seats.Touch(me, touched);
        }

        seats.Sweep(now);

        return me is null ? null : seats.Held(project.Name.Value, me);
    }

    /// <summary>What is wrong with a name somebody has typed, as they type it.</summary>
    /// <param name="typed">What is in the field.</param>
    /// <returns>The reason, or null when there is none.</returns>
    /// <remarks>
    /// Only the shape is checked here. Whether the registry already holds the name for a real
    /// agent is a question for the broker and is asked by <c>SeatBook</c> when the seat is taken;
    /// checking it on every keystroke would be a call per character.
    /// </remarks>
    internal static string? NameProblem(string typed) =>
        Seat.TryRead(typed, out _, out var why) ? null : why ?? "not a name";

    /// <summary>How one row reads.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The line, with every fact in words.</returns>
    private string Line(AgentRow row)
    {
        // The phrase comes from the shared derivation so the console says the same thing about the
        // same agent. The row's own State is still what the withdrawal dialog quotes back, because
        // that one has to match what a person read when they decided.
        var standing = _standing.FirstOrDefault(s => s.Name == row.Name)?.Standing ?? row.State;

        return $"{row.Name}  [{Safe.Text(standing)}]  {row.Owns}  {row.Workspace}";
    }

    /// <summary>What the shared derivation says about each agent, in the order drawn.</summary>
    internal IReadOnlyList<AgentStanding> Standings => _standing;

    /// <summary>The standing of whichever agent the person is on, or null.</summary>
    internal AgentStanding? SelectedStanding =>
        Selected is { } row ? _standing.FirstOrDefault(s => s.Name == row.Name) : null;
}

/// <summary>One agent, as this region shows it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="State">Its state, in words, from the registry and the catalogue together.</param>
/// <param name="Tier">Its tier, or a mark when only the catalogue knows it.</param>
/// <param name="Owns">What it says it is for, or that it has said nothing.</param>
/// <param name="Workspace">Where it runs, as the registry or the catalogue records it.</param>
/// <param name="Seat">Whether this is a person's seat rather than an agent.</param>
/// <remarks>
/// <see cref="Seat"/> is carried rather than derived again by each caller because it decides what
/// may be done to a row: a seat is never offered for retiring or purging, whoever holds it.
/// </remarks>
internal sealed record AgentRow(
    string Name,
    string State,
    string Tier,
    string Owns,
    string Workspace,
    bool Seat);
