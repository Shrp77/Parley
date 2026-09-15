using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace Parley.Terminal;

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

    /// <summary>The heading the seats are grouped under.</summary>
    /// <remarks>
    /// Their own group rather than a state they share with software. A seat is a person, and filing
    /// one under a state a process can be in would answer a different question from the one being
    /// asked. No standing is quoted here on purpose: <c>SurfaceAgreementTests</c> forbids either
    /// surface writing one as a literal, and it does not read a comment differently from code —
    /// which is what makes it a check rather than a habit.
    /// </remarks>
    internal const string Seats = "seats — people, not agents";

    /// <summary>The heading a withdrawn seat is grouped under.</summary>
    /// <remarks>
    /// <b>Apart from the live ones, and the owner's own deployment is why.</b> Both seats on it
    /// were retired, and filing them with the takeable ones made a seat that the broker will refuse
    /// look exactly like one it will not. Enter still explains it, but a list that has to be acted
    /// on to be understood is a list that has not said what it knows.
    /// </remarks>
    internal const string RetiredSeats = "seats — withdrawn, take one to put it back";

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

    private readonly GuardedList _list = Lists.Filling(Colours.Agents);

    private List<AgentRow> _rows = [];
    private List<AgentStanding> _standing = [];

    /// <summary>
    /// What each drawn line is: an agent, or a heading and no agent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A map rather than an offset, and the offset is what grouping broke.</b> This used to be a
    /// single number — one when a broker's complaint occupied the first line, zero otherwise — and
    /// <c>Selected</c> subtracted it. Grouping puts a heading above each set of agents, so the
    /// distance between a line and its agent is no longer constant, and a version that kept
    /// subtracting would name the agent above the one highlighted. In this region that is the
    /// difference between retiring the right agent and the wrong one.
    /// </para>
    /// <para>
    /// Null for a heading and for a broker's complaint, which is what makes both unselectable
    /// without a second list of exceptions to keep in step.
    /// </para>
    /// </remarks>
    private List<AgentRow?> _at = [];

    /// <summary>Whether the first line is a broker's complaint rather than a heading.</summary>
    /// <remarks>
    /// Held rather than inferred from the text, because inferring it would mean matching a
    /// sentence the broker wrote against the headings this surface writes, and a broker whose
    /// complaint happened to read like a heading would be coloured as one.
    /// </remarks>
    private bool _complained;

    /// <summary>What colour each drawn line earns when it is a heading, null when it is not.</summary>
    /// <remarks>
    /// <b>A heading takes the colour of the state it names.</b> It would have been simpler to draw
    /// every heading alike, and <c>ColourSaysNothingNewTests</c> said why not: two states a person
    /// has to tell apart must not be the same colour. The heading <i>is</i> the state now, so it is
    /// the thing that has to carry it — and the rule still holds the way it always did, because the
    /// state is also written out in words and survives the colour being stripped.
    /// </remarks>
    private List<Color?> _tone = [];

    internal AgentsRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        SetScheme(Colours.Region(Colours.Agents));
        Add(_list);
        Lists.Tint(_list, Tint);

        // Headings and a broker's complaint are lines with no agent behind them, and the map says
        // which those are. Read through the same field `Selected` reads, so the cursor can never
        // rest somewhere this region would answer null for.
        _list.Skip = row => row >= 0 && row < _at.Count && _at[row] is null;
    }

    /// <summary>The rows as drawn, in order.</summary>
    internal IReadOnlyList<AgentRow> Rows => _rows;

    /// <summary>Whichever row the person is on, or null when they are on a heading.</summary>
    /// <remarks>
    /// Read through <see cref="_at"/> rather than by arithmetic. Headings and a broker's complaint
    /// are lines with no agent behind them, so "which agent is highlighted" has a null answer for
    /// some lines and no amount of index maths makes it otherwise.
    /// </remarks>
    internal AgentRow? Selected =>
        _list.SelectedItem is { } at && at >= 0 && at < _at.Count ? _at[at] : null;

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

            // A catalogue is keyed by role; this asked whether a set of roles held `role@machine`,
            // which it never does, so every registered agent was told it was not in the catalogue.
            if (!seat
                && !(catalogued.Contains(agent.Name.Role)
                     && string.Equals(agent.Name.Machine, NodeCatalogue.Machine, StringComparison.Ordinal)))
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

        // Roles registered *here*, compared as roles. This built a set of `role@machine` and asked
        // whether it held a bare role, which it never does, so every catalogued role was listed a
        // second time beside its own registration.
        var registered = new HashSet<string>(
            (registry ?? [])
                .Where(a => string.Equals(a.Name.Machine, NodeCatalogue.Machine, StringComparison.Ordinal))
                .Select(a => a.Name.Role),
            StringComparer.Ordinal);

        foreach (var role in catalogued.Where(role => !registered.Contains(role)))
        {
            // A catalogue holds roles, so the machine has to be put back before this is a name.
            // Parsing the role alone never succeeded, which is why every catalogued agent showed no
            // workspace at all -- the one column saying where it would actually run.
            var workspace = AgentName.TryCreate($"{role}@{NodeCatalogue.Machine}", out var qualified)
                ? catalogue.WorkspaceFor(qualified)
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

        // Built into locals and swapped in together. Assigned to the fields as they are computed,
        // a draw landing between two of them would colour this list from one registry while
        // reading its rows from another -- and `Selected` would name an agent by an offset
        // belonging to the previous read, which in this region is what a purge acts on.
        var rows = Join(registry, catalogue);
        var standing = Standing.Join(
            registry,
            new HashSet<string>(catalogue.Roles, StringComparer.Ordinal),
            Seat.Adapter,
            NodeCatalogue.Machine);

        var lines = new List<string>();
        var map = new List<AgentRow?>();
        var tone = new List<Color?>();

        if (problem is not null)
        {
            // Both, rather than one or the other. The catalogue is still true when the broker is
            // not answering, and saying only "not answering" would hide the half of the picture
            // this machine can still see.
            lines.Add(Safe.Text(problem));
            map.Add(null);
            tone.Add(Colours.Bad);
        }

        if (rows.Count == 0)
        {
            if (problem is null)
            {
                lines.Add(Nothing);
                map.Add(null);
                tone.Add(null);
            }
        }
        else
        {
            foreach (var group in Grouped(rows, standing))
            {
                lines.Add(group.Heading);
                map.Add(null);
                tone.Add(Shade(group.Rows[0], standing));

                foreach (var row in group.Rows)
                {
                    lines.Add(Line(row));
                    map.Add(row);
                    tone.Add(null);
                }
            }
        }

        // The selection follows the agent rather than the position. A refresh that moved somebody's
        // highlight onto a different agent is how a destructive action gets taken against the wrong
        // one, and this region is where the destructive actions are.
        //
        // Grouping makes that matter more rather than less: an agent whose state changes moves to a
        // different group, so its line number changes for a reason that has nothing to do with the
        // person's cursor.
        var at = keep is null ? -1 : map.FindIndex(row => row?.Name == keep);

        // The map goes in with the rows, under the list's guard. A moment where one has been
        // swapped and the other has not is a moment where this region names the wrong agent.
        _list.Replace(
            [.. lines],
            at >= 0 ? at : map.FindIndex(row => row is not null) is var first && first >= 0 ? first : null,
            also: () =>
            {
                _rows = [.. rows];
                _standing = [.. standing];
                _at = map;
                _tone = tone;
                _complained = problem is not null;
            });
    }

    /// <summary>One heading and the agents under it.</summary>
    /// <param name="Heading">What the group is called.</param>
    /// <param name="Rows">Its agents, alphabetically.</param>
    internal sealed record AgentGroup(string Heading, IReadOnlyList<AgentRow> Rows);

    /// <summary>Groups agents by where they stand, seats first.</summary>
    /// <param name="rows">Every agent.</param>
    /// <param name="standings">What the shared derivation says about each.</param>
    /// <returns>The groups, in the order they are drawn.</returns>
    /// <remarks>
    /// <para>
    /// <b>The state moved from the row to the heading because it did not fit.</b> A line was
    /// <c>name [standing] owns workspace</c>, and in the half-width pane a project view gives this
    /// region, a 27-character agent name and a 50-character standing are over the width before the
    /// ownership line begins — so the one fact a person is looking for was the one cut off. Said
    /// once above a group, it is always readable, and the rows below it are short enough to be
    /// scanned.
    /// </para>
    /// <para>
    /// <b>Seats first, and their own group.</b> A seat is a person rather than an agent, so
    /// grouping it under a state it shares with software would answer a different question from
    /// the one being asked. It is also what somebody looks for first, being their own.
    /// </para>
    /// <para>
    /// The remaining groups are ordered by how much they ask of the reader: what is running, what
    /// could be, what will not be, and what an operator withdrew. Alphabetical within a group,
    /// because the order of two agents in the same state carries no information and a stable order
    /// is what lets an eye return to where it was.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<AgentGroup> Grouped(
        IReadOnlyList<AgentRow> rows,
        IReadOnlyList<AgentStanding> standings)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(standings);

        var groups = rows
            .GroupBy(row => Heading(row, standings.FirstOrDefault(s => s.Name == row.Name)))
            .Select(group => new AgentGroup(
                group.Key,
                [.. group.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)]))
            .ToList();

        return [.. groups.OrderBy(group => Rank(group.Heading)).ThenBy(
            group => group.Heading, StringComparer.Ordinal)];
    }

    /// <summary>Which group one agent belongs in.</summary>
    /// <param name="row">The agent.</param>
    /// <param name="standing">What the shared derivation says about it, when it says anything.</param>
    /// <returns>The heading.</returns>
    /// <remarks>
    /// A seat is filed by whether it can be taken rather than by a state software can be in, and
    /// everything else takes the shared derivation's own phrase — so a reworded standing moves its
    /// group rather than quietly growing a second one beside it.
    /// </remarks>
    private static string Heading(AgentRow row, AgentStanding? standing) =>
        row.Seat
            ? standing is { Retired: true } ? RetiredSeats : Seats
            : standing?.Standing ?? row.State;

    /// <summary>What colour a group's heading earns from the state it names.</summary>
    /// <param name="first">Any agent in the group; they all stand in the same place.</param>
    /// <param name="standings">What the shared derivation says about each agent.</param>
    /// <returns>The colour, or null when the state earns none.</returns>
    /// <remarks>
    /// Read from the typed standing rather than from the heading's words, for the reason the row
    /// colours were: these facts arrive as booleans, and matching the words back out would be a
    /// second derivation of something the shared one already decided.
    /// </remarks>
    private static Color? Shade(AgentRow first, IReadOnlyList<AgentStanding> standings)
    {
        var standing = standings.FirstOrDefault(s => s.Name == first.Name);

        return standing is null ? null
            : standing.Seat ? Colours.Seated
            : standing.Retired ? Colours.Bad
            : standing.Running ? Colours.Good
            : standing.StartableHere ? Colours.Warn
            : Colours.Quiet;
    }

    /// <summary>Where a group sits in the order.</summary>
    /// <param name="heading">The group's name.</param>
    /// <returns>Its rank, lower first.</returns>
    /// <remarks>
    /// Matched against the shared derivation's own phrases rather than re-deriving the state, so a
    /// reworded standing moves its group rather than silently falling to the end. Anything
    /// unrecognised sorts between "can be started" and "retired", which is where a state nobody has
    /// ranked yet is least misleading.
    /// </remarks>
    private static int Rank(string heading) => heading switch
    {
        Seats => 0,
        RetiredSeats => 1,
        Standing.Running => 2,
        Standing.NeverRun => 3,
        Standing.Idle => 4,
        Standing.OnlyWhenOpened => 6,
        Standing.NoMachine => 7,
        Standing.NeverRunStuck => 8,
        Standing.Retired => 10,
        _ => 5,
    };

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
    /// <returns>The line: the agent's name, indented under its group.</returns>
    /// <remarks>
    /// <para>
    /// <b>The name and nothing else, which is the change.</b> It was
    /// <c>name [standing] owns workspace</c> — four facts on a line in a pane half a screen wide,
    /// where the standing was routinely cut off. The standing is the group's heading now, and the
    /// ownership line and workspace are on the agent's own screen rather than crushed into a list.
    /// </para>
    /// <para>
    /// Indented by two, so a heading sits at column zero and an agent does not. That is the same
    /// rule the transcript uses to tell a header from a payload, and it survives colour being
    /// stripped — which is what makes it a rule rather than a decoration.
    /// </para>
    /// </remarks>
    private static string Line(AgentRow row) => "  " + row.Name;

    /// <summary>What colour a row earns from where its agent stands.</summary>
    /// <param name="row">Which row.</param>
    /// <returns>The colour, or null for a row that is not an agent.</returns>
    /// <remarks>
    /// <para>
    /// <b>State rather than identity here, and identity rather than state in the transcript.</b>
    /// <see cref="Palette"/> gives every agent a colour of its own and says what it is for: helping
    /// an eye follow one agent down a long conversation. That is the transcript's problem. The
    /// question this region answers is whether a peer can be asked anything right now, so the
    /// colour answers that one — eight identity hues here would be eight colours saying nothing
    /// about the only thing somebody is looking at the list to find out.
    /// </para>
    /// <para>
    /// Read from the typed standing rather than from the drawn phrase, because these facts arrive
    /// as booleans and matching the words back out would be a second derivation of something the
    /// shared one already decided.
    /// </para>
    /// </remarks>
    private Color? Tint(int row)
    {
        if (row < 0 || row >= _at.Count)
        {
            return null;
        }

        // A heading, or the reason the broker could not be asked. Both are lines with no agent
        // behind them, and they do not earn the same colour: the complaint is bad news, and a
        // heading is this surface's own word for a state rather than a fact about any one agent.
        // A heading, or the reason the broker could not be asked. A heading names a state and
        // takes that state's colour; the complaint is bad news and takes the colour of bad news.
        if (_at[row] is not { } here)
        {
            return row < _tone.Count ? _tone[row] : null;
        }

        // **An agent under a heading takes no colour of its own, and that is the rule rather than
        // a saving.** The heading above it already says where it stands, in words and in colour;
        // painting the same fact onto every row beneath would be a colour that says nothing new,
        // which is the one thing Colours forbids. It was caught by the test that forbids it.
        return null;
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
