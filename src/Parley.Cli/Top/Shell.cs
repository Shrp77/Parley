using System.Drawing;
using System.Text;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Headless;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Cli.Top;

/// <summary>
/// The whole surface: four regions, a status line, and the reads that fill them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything at once, rather than one question at a time.</b> 004's terminal was a sequence
/// of prompts and read as a wizard; what a person looking at a deployment wants is the projects,
/// the agents and a conversation in front of them together, and to act on what is in front of
/// them. That is the whole reason this feature exists.
/// </para>
/// <para>
/// <b>Side by side above the threshold, tabbed below it.</b> The same four view objects either
/// way, so a test that found a region by its title finds it in both — and the 004 defect this
/// guards against was found by a width test rather than by looking: a recipient column was
/// silently dropped at forty columns and nothing said so.
/// </para>
/// <para>
/// <b>The reads are the caller's to trigger.</b> Nothing here polls. A surface that refreshed on
/// a timer would move somebody's selection under their hand, and the one thing that must survive
/// a redraw is where the person is — which is why each region keeps its selection by name rather
/// than by position.
/// </para>
/// </remarks>
internal sealed class Shell : Window
{
    /// <summary>The width at which the four regions stop fitting side by side.</summary>
    /// <remarks>
    /// <para>
    /// A test drives one column either side of it rather than trusting it. The number is a constant
    /// rather than a calculation because a layout that rearranged itself at a width nobody could
    /// name would be untestable at its own boundary.
    /// </para>
    /// <para>
    /// <b>Eighty, and it was two hundred for one release for a reason worth keeping.</b> The report
    /// that dragging a window narrow stacked nothing was first read as a terminal that would not go
    /// below eighty columns. It was not: nothing called <see cref="Lay"/> after the constructor, so
    /// the surface could not stack at any width. Two hundred was a diagnostic that let a walk reach
    /// the boundary on any window at all; the stacked layout has been seen working, so the number
    /// is back to the one a person would want — a project's two panes side by side need about that
    /// much before either is worth reading.
    /// </para>
    /// </remarks>
    internal const int Threshold = 80;

    /// <summary>What the status line always says, before the region's name.</summary>
    internal const string FocusPrefix = "Focus: ";

    /// <summary>
    /// The key that lists what the focused region can do.
    /// </summary>
    /// <remarks>
    /// It is also how the actions are reachable by mouse: the list it opens is a menu, and every
    /// entry in it is selectable with the pointer. contracts/surface.md asks for exactly that — no
    /// action exists only as a key — and a status line is not clickable.
    /// </remarks>
    internal const char ActionsKey = '?';

    /// <summary>What the menu of actions is called.</summary>
    internal const string ActionsTitle = "What this region can do";

    /// <summary>
    /// How the status line names the key that goes back.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived from <c>Key.Esc</c>, whose own rendering is the toolkit's
    /// business and has been <c>Esc</c>, <c>Escape</c> and <c>ESC</c> across versions. What is on
    /// the status line has to match what is on somebody's keyboard, not what a library calls it.
    /// </remarks>
    internal const string BackStroke = "Esc";

    private readonly BrokerDirectory _directory;
    private readonly Label _status = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
    private readonly List<View> _regions;
    private readonly CancellationTokenSource _reading = new();

    private bool _stacked;
    private bool _closed;
    private ViewPlace _place = ViewPlace.Top;

    /// <summary>How the conversations list is ordered, and whether archived ones are in it.</summary>
    /// <remarks>
    /// View state, remembered for the session and never sent to the broker. It is a preference
    /// about looking rather than a fact about the deployment, so two people reading the same
    /// project may sort it differently without arguing.
    /// </remarks>
    private SortBy _sort = SortBy.Activity;
    private bool _showArchived;
    private int _archivedOmitted;
    private readonly Lock _chain = new();

    /// <summary>Guards the view tree, which two threads can reach.</summary>
    /// <remarks>
    /// <b>Not the same lock as the work chain, and not paranoia.</b> A change of view is reachable
    /// from two places: <c>OnKeyDown</c>, on the loop, and the queued handler that opens a
    /// conversation, on a pool thread. Both call <see cref="Arrange"/>, which adds and removes
    /// subviews -- and Terminal.Gui's subview list is an ordinary list. Two of them at once throws
    /// out of <c>List.CopyTo</c> with a message about array bounds, which is what a probe of
    /// sixty-four concurrent navigations produced.
    /// <para>
    /// Separate from <see cref="_chain"/> because that one is held while building the next link of
    /// the work chain, and a single lock covering both would have a queued item waiting on the
    /// loop while the loop waited on the queue.
    /// </para>
    /// </remarks>
    private readonly Lock _screen = new();
    private Task _work = Task.CompletedTask;
    private DateTimeOffset _touched = DateTimeOffset.UtcNow;

    internal Shell(BrokerDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _directory = directory;

        Title = "parley";
        BorderStyle = LineStyle.None;

        Projects = new ProjectsRegion();
        Agents = new AgentsRegion();
        Conversations = new ConversationsRegion();
        Transcript = new TranscriptRegion();

        _regions = [Projects, Agents, Conversations, Transcript];

        // Choosing and opening are things a person did, whether by key or by pointer, so both
        // count as being here. A mouse-only session would otherwise be swept mid-use.
        Projects.Chose += (_, _) => Touched();
        Conversations.Opened += (_, _) => Touched();

        Projects.Chose += (_, project) => Queue(token => SelectAsync(project, token));

        // **Opening a conversation is now a change of view as well as a read.** The read still
        // happens the same way -- the transcript region fills from the broker -- but the surface
        // moves into the conversation view rather than showing it in a fourth pane beside three
        // others. Both halves are queued together so a test awaiting Settled sees a finished
        // screen rather than a half-drawn one.
        Conversations.Opened += (_, conversation) => Queue(async token =>
        {
            // **The project the view is about, not whichever the projects list is on.** They are
            // the same until the projects list is off screen, which is exactly when this runs: a
            // conversation is opened from inside a project, and the list that chose that project
            // was taken off the screen to make room for it. Reading the list here made this depend
            // on a region nobody is looking at.
            var project = Current;

            if (project is null)
            {
                return;
            }

            await OpenAsync(project, conversation.Id, token).ConfigureAwait(false);
            await GoAsync(_place.Viewing == Viewing.Project
                ? _place.Into(conversation.Id)
                : _place).ConfigureAwait(false);
        });

        // A bar rather than a line in the terminal's own colours, and the one place this surface
        // paints a background of its own. It is the only thing on screen that is always there, and
        // what it says -- where you are, how to leave, what this region can do -- is what somebody
        // who is lost reads. A conventional bar is found without being looked for.
        _status.SetScheme(Colours.StatusBar());

        Add(_status);
        Lay(Threshold);
        Focus(0);
    }

    /// <summary>The projects region.</summary>
    internal ProjectsRegion Projects { get; }

    /// <summary>The agents region.</summary>
    internal AgentsRegion Agents { get; }

    /// <summary>The conversations region.</summary>
    internal ConversationsRegion Conversations { get; }

    /// <summary>The transcript region.</summary>
    internal TranscriptRegion Transcript { get; }

    /// <summary>Which pane of the current view has focus.</summary>
    internal int FocusedRegion { get; private set; }

    /// <summary>Which of the three views the person is on, and what it is about.</summary>
    internal ViewPlace Place => _place;

    /// <summary>
    /// The panes of the view currently open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One view at a time, which is the whole of 006.</b> 005 put all four of these on one
    /// screen and moved between them with Tab; the owner ran it and called that "confusing and too
    /// busy", which is a verdict on the arrangement rather than on anything underneath it. So the
    /// same four view objects are kept — a selection, a scroll position and a test's reference to a
    /// region all survive — and only the ones belonging to the current view are on screen.
    /// </para>
    /// <para>
    /// Tab still moves between panes, but a view has one or two rather than four, so it is a
    /// smaller question with a visible answer.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<View> Panes => _place.Viewing switch
    {
        Viewing.Project => [Agents, Conversations],
        Viewing.Conversation => [Transcript],
        _ => [Projects],
    };

    /// <summary>Whether the regions are stacked as tabs rather than laid side by side.</summary>
    internal bool Stacked => _stacked;

    /// <summary>What the status line says.</summary>
    internal string Status => _status.Text;

    /// <summary>
    /// Every read a keystroke started, so a caller can wait for the screen to be true.
    /// </summary>
    /// <remarks>
    /// A key press is synchronous and the read it causes is not. Production does not care — the
    /// region redraws when the read returns — but a test that asserted on the screen immediately
    /// after pressing Enter would be asserting on the screen before the answer arrived, and would
    /// pass or fail by timing. Awaiting this is what makes a driven journey deterministic.
    /// </remarks>
    internal Task Settled
    {
        get
        {
            lock (_chain)
            {
                return _work;
            }
        }
    }

    /// <summary>How long a read started by a keystroke may take before it is abandoned.</summary>
    /// <remarks>
    /// Only relevant to the token this hands its own reads. A broker that never answers is already
    /// contained by <see cref="BrokerDirectory.ReadAsync{T}"/>; this exists so a surface being torn
    /// down does not wait on one.
    /// </remarks>
    internal CancellationToken Reading => _reading.Token;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>This is what makes the layout answer to the terminal.</b> Until it existed
    /// <see cref="Lay"/> was called once, from the constructor, with <see cref="Threshold"/> itself
    /// — so the surface was laid out side by side at construction and stayed that way for the life
    /// of the process. Dragging a window to forty columns stacked nothing, because nothing asked
    /// it to.
    /// </para>
    /// <para>
    /// Every test drove <see cref="Lay"/> directly, which is why a suite with a stacking test, a
    /// narrow-width test and a dragged-narrow-and-back test all passed against a surface that
    /// could not stack. They tested the arrangement; nothing tested that anything performs it.
    /// </para>
    /// <para>
    /// Re-entrancy is not a hazard here: <see cref="Lay"/> reparents views, which changes frames,
    /// which arrives back here — and finds the arrangement already the one being asked for, so it
    /// returns without touching anything.
    /// </para>
    /// </remarks>
    protected override void OnFrameChanged(in Rectangle frame)
    {
        base.OnFrameChanged(in frame);

        if (frame.Width > 0)
        {
            Lay(frame.Width);
        }
    }

    /// <summary>Arranges the regions for a width.</summary>
    /// <param name="width">How wide the screen is.</param>
    /// <remarks>
    /// The four views are reparented rather than rebuilt, so a selection, a scroll position and a
    /// test's reference to a region all survive a resize. Rebuilding them would lose the person's
    /// place every time somebody dragged a window edge.
    /// </remarks>
    internal void Lay(int width)
    {
        var stacked = width < Threshold;

        if (stacked == _stacked && SubViews.Count > 1)
        {
            return;
        }

        _stacked = stacked;
        Arrange();
    }

    /// <summary>Rebuilds the current view's panes whatever the width has done.</summary>
    /// <remarks>
    /// Split out from <see cref="Lay"/> because that one returns early when the arrangement has not
    /// changed, which is right for a resize and wrong for a change of view: the panes are different
    /// even though the width is the same.
    /// </remarks>
    private void Relay() => Arrange();

    /// <summary>Puts the current view's panes on screen and takes the others off.</summary>
    private void Arrange()
    {
        lock (_screen)
        {
            ArrangeLocked();
        }
    }

    /// <summary>Arranges, with the screen already held.</summary>
    private void ArrangeLocked()
    {
        var stacked = _stacked;

        foreach (var region in _regions)
        {
            region.SuperView?.Remove(region);
        }

        // **Only the current view's panes go on screen.** The other regions are kept alive rather
        // than rebuilt, so a selection, a scroll position and a test's reference to one all survive
        // a walk into a project and back out.
        var panes = Panes;

        foreach (var pane in _regions)
        {
            pane.Visible = panes.Contains(pane);
        }

        if (panes.Count == 1)
        {
            var only = panes[0];

            only.X = 0;
            only.Y = 0;
            only.Width = Dim.Fill();
            only.Height = Dim.Fill(1);
        }
        else if (stacked)
        {
            // Narrow: one pane at a time, and the status line says which of how many.
            //
            // **The toolkit's tab strip was tried in 005 and is what the specification forbids.**
            // Titles across the top of a forty-column terminal do not fit, and what the strip does
            // about that is cut them: `Conversations` became `Conversat`, silently. So each pane
            // fills the space, its own frame carries its title in full, and the status line carries
            // the rest.
            foreach (var pane in panes)
            {
                pane.X = 0;
                pane.Y = 0;
                pane.Width = Dim.Fill();
                pane.Height = Dim.Fill(1);
            }
        }
        else
        {
            // Two panes, side by side. Agents on the left because the question a project view
            // answers first is who is in it; conversations wider because they carry identifiers and
            // timestamps rather than short names.
            panes[0].X = 0;
            panes[0].Y = 0;
            panes[0].Width = Dim.Percent(40);
            panes[0].Height = Dim.Fill(1);

            panes[1].X = Pos.Right(panes[0]);
            panes[1].Y = 0;
            panes[1].Width = Dim.Fill();
            panes[1].Height = Dim.Fill(1);
        }

        foreach (var pane in panes)
        {
            Add(pane);
        }

        Focus(FocusedRegion);
    }

    /// <summary>Moves focus to a region and says so.</summary>
    /// <param name="at">Which region, counted from the projects.</param>
    internal void Focus(int at)
    {
        lock (_screen)
        {
            FocusLocked(at);
        }
    }

    /// <summary>Moves focus, with the screen already held.</summary>
    /// <param name="at">Which pane.</param>
    private void FocusLocked(int at)
    {
        var panes = Panes;

        FocusedRegion = ((at % panes.Count) + panes.Count) % panes.Count;

        var region = panes[FocusedRegion];

        // Narrow, two panes become one at a time. With one pane there is nothing to hide, and
        // hiding it would leave an empty screen.
        if (_stacked && panes.Count > 1)
        {
            foreach (var other in panes)
            {
                other.Visible = ReferenceEquals(other, region);
            }
        }

        region.SetFocus();

        // Stacked, the line says which of how many as well as which. A person who has pulled their
        // window in can otherwise see no evidence that the surface has four regions at all, which
        // reads as three of them having been lost to the resize. Naming the other three outright
        // does not fit in forty columns, and a status line clipped mid-word would be the same
        // silent truncation this layout exists to avoid.
        var actions = Actions();

        var focus = _stacked && panes.Count > 1
            ? $"{FocusPrefix}{NameOf(region)} ({FocusedRegion + 1}/{panes.Count}, Tab)"
            : FocusPrefix + NameOf(region);

        // Where in the deployment, not only which pane. A view showing one project's agents looks
        // exactly like another's, and 005's status line -- which only ever had one screen to
        // describe -- would now be describing three.
        var placed = _place.Viewing == Viewing.Projects ? focus : $"{_place.Title} / {focus}";

        // **The ways out are written next to where you are.** Escape has gone back since 006 was
        // built and nothing on screen ever said so: the owner walked into a project and reported
        // there was no way back to the list. Ctrl+Q was worse -- it was named in a comment and
        // bound nowhere. Both are here, and the menu holds both, so neither is ever only a key.
        var back = BackLabel is { } label ? "  " + label : string.Empty;
        var quit = "  " + QuitLabel;

        var listed = actions.Count == 0
            ? string.Empty
            : "  " + string.Join("  ", actions.Select(Caption));

        var menu = $"  {ActionsKey}: actions";

        // The last rung that still names the key. `Focus: Conversations (2/2, Tab)` is thirty-one
        // columns of a forty-column terminal, which leaves nine — enough for this and not for the
        // word "actions". Measured on the stacked project view, which is the longest focus text
        // this surface produces.
        var brief = $"  {ActionsKey}: more";
        var room = Viewport.Width > 0 ? Viewport.Width : Threshold;

        // **Listed in full when they fit, and behind one key when they do not**, and then further
        // down a ladder that ends holding the two things that matter most: which pane has focus,
        // and that `?` exists. A status line clipped by the toolkit at whatever column the terminal
        // ends at is the silent truncation the narrow layout exists to avoid, arrived at from the
        // other direction.
        //
        // **The ladder replaced a single either/or, which the way-back label had just broken.** At
        // forty columns `alpha / Focus: Agents  Esc: back to alpha` is over the width before an
        // action is named, and nothing caught it: the guard for this drives the projects view,
        // which has no back label, so it only failed once Ctrl+Q pushed the *top* view one column
        // over as well.
        //
        // Dropping back and quit before the menu is deliberate rather than reluctant. `?` opens a
        // list that holds both of them, so the short form is not a screen with no way out -- it is
        // the same way out, one keystroke further away, which is the trade the actions themselves
        // already make.
        _status.Text = Fits(
            room,
            placed + back + quit + listed,
            placed + back + quit + menu,
            placed + menu,
            focus + menu,
            focus + brief,
            focus);
    }

    /// <summary>The first of several status lines that fits, or the shortest when none does.</summary>
    /// <param name="room">How many columns there are.</param>
    /// <param name="candidates">The lines, longest and most informative first.</param>
    /// <returns>The one to draw.</returns>
    /// <remarks>
    /// Falling back to the last rather than to the empty string: a terminal narrower than
    /// <c>Focus: Conversations</c> is one where something has to be cut, and cutting to nothing
    /// would leave a person with no evidence the status line exists at all.
    /// </remarks>
    private static string Fits(int room, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Length <= room)
            {
                return candidate;
            }
        }

        return candidates[^1];
    }

    /// <summary>Leaves the surface.</summary>
    /// <remarks>
    /// <para>
    /// Asks nothing first. Every destructive act on this surface is already behind a description
    /// and a name typed back, so there is nothing unsaved here to lose — and a seat is released by
    /// leaving rather than in spite of it: <c>SeatBook</c> is disposed on the way out, which ends
    /// the attach streams, which is how §4.0 says a broker learns somebody has gone.
    /// </para>
    /// <para>
    /// A dialog open on top of this one takes the key first and closes itself with it, so this is
    /// only ever reached from a view.
    /// </para>
    /// </remarks>
    internal void Quit() => App?.RequestStop();

    /// <summary>How going back is named on the status line and in the menu, or null at the top.</summary>
    /// <remarks>
    /// It names where it leads rather than only that it goes back. A person who has walked
    /// projects to a project to a conversation cannot otherwise tell one step out from all the way
    /// out, and the two are different keystrokes away from what they were reading.
    /// </remarks>
    internal string? BackLabel =>
        _place.CanGoBack ? $"{BackStroke}: back to {_place.Back().Title}" : null;

    /// <summary>How one action is written wherever it is offered.</summary>
    /// <param name="action">The action.</param>
    /// <returns>Its key and its label.</returns>
    /// <remarks>
    /// One function so the status line and the menu cannot write it two ways — the menu matches
    /// the entry the person chose back against the action by this string, so two spellings would
    /// be a menu whose entries silently do nothing.
    /// </remarks>
    private static string Caption(RegionAction action) => $"{action.Key}: {action.Label}";

    /// <summary>Moves focus to the next pane, or the previous one.</summary>
    /// <param name="forwards">Whether to go forwards.</param>
    internal void Move(bool forwards) => Focus(FocusedRegion + (forwards ? 1 : -1));

    /// <summary>Opens whatever is selected, one level in.</summary>
    /// <returns>When the view has been opened and its reads started.</returns>
    /// <remarks>
    /// Chosen from the pane that shows the thing: a project from the projects view, a conversation
    /// from the project view. A transcript has nothing below it, so this does nothing there rather
    /// than inventing a fourth level.
    /// </remarks>
    internal Task OpenAsync() => _place.Viewing switch
    {
        Viewing.Projects when Projects.Selected is { } project => GoAsync(ViewPlace.Into(project.Name)),
        Viewing.Project when Conversations.Selected is { } conversation =>
            GoAsync(_place.Into(conversation.Id)),
        _ => Task.CompletedTask,
    };

    /// <summary>Goes back one level.</summary>
    /// <returns>When the view has been changed.</returns>
    /// <remarks>
    /// <b>Back keeps the place rather than the level.</b> Coming out of a conversation lands on the
    /// project it was in with that conversation still highlighted, because each region keeps its
    /// selection by name across a redraw. Reading three conversations in turn should cost three
    /// keystrokes, not three descents from the top.
    /// </remarks>
    internal Task BackAsync() =>
        _place.CanGoBack ? GoAsync(_place.Back()) : Task.CompletedTask;

    /// <summary>Moves to a view and lays it out.</summary>
    /// <param name="place">Where to go.</param>
    /// <returns>When the view is on screen.</returns>
    /// <remarks>
    /// Internal rather than private so a test can put the surface on a view without first building
    /// a deployment to navigate through. One test still walks in the way a person does -- select,
    /// Enter -- because a suite that only ever arrived by this route could not tell that the route a
    /// person takes had stopped working.
    /// </remarks>
    internal Task GoAsync(ViewPlace place)
    {
        lock (_screen)
        {
            _place = place;
        }

        Touched();

        // Relaid unconditionally: the panes have changed even though the width has not, so Lay's
        // usual early return -- same arrangement, already built -- would leave the previous view on
        // screen while the status line said otherwise.
        Relay();
        Focus(0);

        return Task.CompletedTask;
    }

    /// <summary>What the focused region can do, and the letter that does each.</summary>
    /// <returns>The actions, in the order the status line lists them.</returns>
    /// <remarks>
    /// Actions belong to the region that shows the thing acted on: a seat is taken from the agents
    /// region, a question is answered from a transcript, a project is created or removed from the
    /// projects region. That is User Story 2's rule, and having the region answer for its own
    /// actions is what keeps the status line and the keys from drifting apart.
    /// </remarks>
    internal IReadOnlyList<RegionAction> Actions() => Panes[FocusedRegion] switch
    {
        // Naming the running deployment is offered only while there is one nothing names yet.
        // Offered always, it would invite somebody to name the same broker twice, which is two
        // projects sharing a store and is what §4.1 forbids.
        ProjectsRegion =>
        [
            new RegionAction('c', Naming.CreateTitle, Create),

            // Offered only while there is a running deployment nothing names yet. Offered always,
            // it would invite somebody to name the same broker twice, which is two projects
            // sharing a store and is what §4.1 forbids.
            .. Naming.CanNameRunning(Projects.Projects, Installed)
                ? new[] { new RegionAction('n', Naming.NameRunningTitle, NameRunning) }
                : [],

            new RegionAction('r', Removing.Title, Remove),
            new RegionAction('g', FollowLogTitle, FollowLog),
            new RegionAction('e', Onboarding.Title, Enrol),
            new RegionAction('b', OpenConsoleTitle, OpenConsole),
        ],

        AgentsRegion =>
        [
            new RegionAction('a', Adding.Title, Add),

            // Never for a seat, whoever holds it: a person leaves a seat, they are not retired
            // (research R7). Withdrawing refuses one reached by any other route as well.
            .. Agents.Selected is { Seat: false } chosen
                ? chosen.State.Contains("retired", StringComparison.Ordinal)
                    ? [new RegionAction('i', Withdrawing.ReinstateTitle, Reinstate)]
                    : new[] { new RegionAction('r', Withdrawing.Title, Withdraw) }
                : Array.Empty<RegionAction>(),

            .. Seats is null
                ? Array.Empty<RegionAction>()
                : Held is null
                    ? [new RegionAction('j', AgentsRegion.JoinTitle, Join)]
                    : new[] { new RegionAction('l', LeaveSeatTitle, LeaveSeat) },
        ],

        // The conversations half of a project view. Sorting and the archived toggle are view
        // state; archiving is reversible and asks nothing; destroying is behind the full door.
        ConversationsRegion =>
        [
            new RegionAction('s', SortTitle, SortConversations),
            new RegionAction('v', _showArchived ? HideArchivedTitle : ShowArchivedTitle, ToggleArchived),

            .. Conversations.Selected is { } chosen
                ? new[]
                {
                    new RegionAction('h', chosen.Archived ? UnarchiveTitle : ArchiveTitle, ArchiveSelected),
                    new RegionAction('d', Destroying.Title, DestroySelected),
                }
                : Array.Empty<RegionAction>(),

            new RegionAction('b', OpenConsoleTitle, OpenConsole),
        ],

        // Taking part needs a seat, because every one of these speaks as somebody. Without one the
        // region still shows a transcript; it simply has no voice to use in it.
        TranscriptRegion when Held is not null =>
        [
            new RegionAction('w', Participation.DeliveryTitle, TakeDelivery),
            new RegionAction('a', Participation.AnswerTitle, Answer),
            new RegionAction('k', Participation.AskTitle, Ask),
            new RegionAction('t', Participation.TellTitle, Tell),
        ],

        _ => [],
    };

    /// <summary>Opens the menu of what the focused region can do.</summary>
    /// <returns>When the menu is open.</returns>
    internal Task Offer()
    {
        var actions = Actions();
        var back = BackLabel;

        // **Going back heads the list and leaving ends it, and between them they are why this now
        // opens on a region with no actions at all.** contracts/surface.md is explicit that no
        // action exists only as a key, and both of these were exactly that: a conversation view
        // held by somebody with no seat offers nothing, so the menu refused to open and the only
        // routes off that screen were two keys nothing named.
        //
        // Back is first because it changes nothing, which is the ordering ChoiceDialog is built on;
        // quitting is last for the same reason read the other way round.
        if (App is not { } app)
        {
            return Task.CompletedTask;
        }

        var menu = Prompting.Choose(
            ActionsTitle,
            NameOf(Panes[FocusedRegion]),
            [.. back is null ? [] : new[] { back }, .. actions.Select(Caption), QuitLabel]);

        var token = Prompting.Open(app, menu);

        menu.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (back is not null && string.Equals(chosen, back, StringComparison.Ordinal))
            {
                _ = BackAsync();

                return;
            }

            if (string.Equals(chosen, QuitLabel, StringComparison.Ordinal))
            {
                Quit();

                return;
            }

            if (actions.FirstOrDefault(a => Caption(a) == chosen) is { } taken)
            {
                _ = taken.Run();
            }
        };

        return Task.CompletedTask;
    }

    /// <summary>The seat this person holds, if they hold one.</summary>
    internal SeatHold? Held { get; private set; }

    /// <summary>
    /// Who this person is, which is not the same question as whether they are seated here.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Held"/> deliberately, and 004 learned this the hard way: taking
    /// "no seat in this project" as the answer to "who are you" meant walking into a project you
    /// were not seated in forgot you, so coming back to the one you <i>were</i> seated in offered
    /// a join while the book still held the seat and the registry still had you present. Only
    /// leaving clears a person.
    /// </remarks>
    internal Seat? Me { get; private set; }

    /// <summary>What is said when a seat is released for want of anybody at the terminal.</summary>
    internal static string Released { get; } =
        $"Your seat was released: nothing touched it for {SeatBook.IdleAfter.TotalMinutes:0} minutes.";

    /// <summary>
    /// Keeps this person's seat alive because they are here, and releases the ones nobody is at.
    /// </summary>
    /// <param name="now">The current instant.</param>
    /// <remarks>
    /// <para>
    /// <b>Called on every draw, and that is what makes the idle rule apply at all.</b> 004 swept on
    /// every visit to its menu; this surface has no menu to visit, so without a hook the seat would
    /// be released only when the process ended — and a terminal left open overnight holding
    /// somebody present in the registry is the exact failure <c>SeatBook.IdleAfter</c> exists to
    /// prevent, reached by a different kind of window than the browser tab it was written for.
    /// </para>
    /// <para>
    /// Being drawn is being here. The sweep therefore cannot take the seat of the person watching
    /// it, only the seats of people who are not — which is the same rule the console applies from
    /// its frame.
    /// </para>
    /// </remarks>
    internal void Tick(DateTimeOffset now)
    {
        if (Seats is null || Projects.Selected is not { } project)
        {
            return;
        }

        var held = AgentsRegion.Refresh(Seats, Me, project, _touched, now);

        if (Me is not null && Held is not null && held is null)
        {
            // Said rather than left to be noticed. A person who came back to a surface that had
            // quietly stopped being them would ask a question as nobody.
            Said = Released;
            Me = null;
        }

        Held = held;
    }

    /// <summary>Where seats are held, or null when this surface cannot seat anybody.</summary>
    internal SeatBook? Seats { get; init; }

    /// <summary>
    /// The manifest this machine's projects are named in, or null when there is none.
    /// </summary>
    /// <remarks>
    /// A directory built from an explicit list is somebody else's list, and inventing a path to
    /// write into would put a project somewhere they were not looking. The actions that write say
    /// so rather than guessing.
    /// </remarks>
    internal string? ManifestPath { get; init; }

    /// <summary>What this machine's supervisor runs, or null for none or unknown.</summary>
    internal InstalledEntry? Installed { get; init; }

    /// <summary>The log view, while one is open.</summary>
    internal LogView? Log { get; private set; }

    /// <summary>Opens the form that creates a project.</summary>
    /// <returns>When the form is open.</returns>
    internal Task Create() =>
        App is { } app && Manifest() is { } manifest
            ? Naming.OpenCreateAsync(app, manifest, Projects.Projects, Installed, Queue, Reporting)
            : Task.CompletedTask;

    /// <summary>Opens the form that names the deployment this machine already runs.</summary>
    /// <returns>When the form is open.</returns>
    internal Task NameRunning()
    {
        if (App is not { } app || Manifest() is not { } manifest)
        {
            return Task.CompletedTask;
        }

        if (Installed is not { Store: not null } entry)
        {
            Said = "This machine has no autostart entry to name, so there is nothing to pre-fill. "
                + $"Use '{Naming.CreateTitle}'.";

            return Task.CompletedTask;
        }

        return Naming.OpenNameRunningAsync(app, manifest, Projects.Projects, entry, Queue, Reporting);
    }

    /// <summary>Opens the dialog that removes a project from this machine.</summary>
    /// <returns>When the dialog is open.</returns>
    internal Task Remove() =>
        App is { } app && Manifest() is { } manifest && Projects.Selected is { } project
            ? Removing.OpenAsync(app, manifest, project, Queue, async (said, gone) =>
            {
                Said = said;

                if (gone)
                {
                    await RefreshAsync(_reading.Token).ConfigureAwait(false);
                }
            })
            : Task.CompletedTask;

    /// <summary>Opens the enrolment dialog.</summary>
    /// <returns>When the dialog is open.</returns>
    internal Task Enrol() =>
        App is { } app ? Onboarding.OpenAsync(app, Queue, Reporting) : Task.CompletedTask;

    /// <summary>Opens the selected project's broker log and follows it.</summary>
    /// <returns>When the view is open.</returns>
    internal Task FollowLog()
    {
        if (App is not { } app || Projects.Selected is not { } project)
        {
            return Task.CompletedTask;
        }

        if (LogView.Open(app, project, Installed, string.Empty, said => Said = said)
            is not var (view, token))
        {
            return Task.CompletedTask;
        }

        Log = view;

        view.Closing += (_, _) =>
        {
            Log = null;

            _ = view.StopAsync();

            Prompting.Close(app, token);
        };

        return Task.CompletedTask;
    }

    /// <summary>Opens the web console in a browser.</summary>
    /// <returns>When it has been asked for.</returns>
    /// <remarks>
    /// The surface keeps running whether or not a browser starts. There is no browser on a headless
    /// machine and no <c>xdg-open</c> on a minimal container, and both are ordinary places to run
    /// this.
    /// </remarks>
    /// <remarks>
    /// <b>The thing on screen, not the front page.</b> A key that always opened the console's home
    /// page would make somebody navigate it from scratch to reach what they were already looking
    /// at, which is worse than having no key. The address comes from <see cref="Deep"/> so the
    /// terminal cannot deep-link to a route the console has renamed — the same defect as the
    /// hand-copied port, one layer up and with a worse symptom, since a 404 reads as the console
    /// being broken rather than the link being wrong.
    /// </remarks>
    private Task OpenConsole()
    {
        Opener(said => Said = said, Deep.For(ConsoleAddress, _place));

        return Task.CompletedTask;
    }

    /// <summary>
    /// How this surface hands an address to a browser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A seam, and it exists because the suite was opening the developer's browser.</b>
    /// <see cref="Launch"/> already records this defect being fixed once: a test called
    /// <c>Launch.Open</c> with a loopback URL to prove the guard let it through, and opened a tab
    /// on every run. The fix there was to test the predicate instead — correct, and it only closed
    /// the route it was looking at. Two tests then pressed <see cref="BrowserKey"/> on a driven
    /// surface, which reaches the same <c>Process.Start</c> through the key rather than through
    /// the function, and the tabs came back. Running the suite in a loop while working on the
    /// terminal is what made it intolerable.
    /// </para>
    /// <para>
    /// <b>An instance property rather than a static hook.</b> Test classes run in parallel and
    /// every one of them builds its own surface, so a static would be the process-global state
    /// this repository already keeps one semaphore to avoid. Nothing in production sets it: the
    /// default is the real launcher, so the shipped path is the one that has no seam in it.
    /// </para>
    /// </remarks>
    internal Func<Action<string>, string, bool> Opener { get; set; } = Launch.Open;

    /// <summary>The key that hands whatever is on screen to a browser.</summary>
    /// <remarks>
    /// A function key because every letter is spoken for by an action and <c>?</c> by the action
    /// list. It is listed among what each view can do rather than left as knowledge somebody has to
    /// have, which is the rule no action exists only as a key.
    /// </remarks>
    internal static Key BrowserKey => Key.F8;

    /// <summary>
    /// The key that leaves the surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is bound here because the toolkit binds it nowhere.</b> <c>TopCommand</c> said in a
    /// comment that "Ctrl+Q leaves the surface properly" and nothing in the suite had ever pressed
    /// it: enumerating what Terminal.Gui 2.4.17 binds at application scope turns up ten entries,
    /// all of them navigation, and no Ctrl+Q among them. The chord did nothing at all.
    /// </para>
    /// <para>
    /// A chord rather than a letter, for <see cref="BrowserKey"/>'s reason — every letter belongs
    /// to an action — and Ctrl+Q rather than anything else because it is what the comment already
    /// promised and what a person coming from any other terminal application will try first.
    /// </para>
    /// </remarks>
    internal static Key QuitKey => Key.Q.WithCtrl;

    /// <summary>How the status line and the menu name leaving.</summary>
    internal const string QuitLabel = "Ctrl+Q: Quit";

    /// <summary>Where the web console listens when a broker hosts it.</summary>
    /// <remarks>
    /// <b>The console's own constant, not a second copy of the number.</b> This was a literal
    /// until it was noticed that the literal was <c>5045</c> and every other statement of the
    /// default — the host, the usage text, the address an autostart entry registers — said
    /// <c>5280</c>. So the one action on this surface that hands off to a browser opened a port
    /// nothing listens on, on every machine, and said <c>Opened …</c> while doing it. A browser
    /// that starts and lands nowhere is the failure <see cref="Launch.Open"/> cannot report,
    /// because from here it succeeded.
    /// </remarks>
    private const string ConsoleAddress = Parley.Web.ConsoleHost.LoopbackAddress;

    /// <summary>What leaving a seat is called.</summary>
    private const string LeaveSeatTitle = "Leave your seat";

    /// <summary>What following the broker's log is called.</summary>
    private const string FollowLogTitle = "Follow the broker log";

    /// <summary>What opening the web console is called.</summary>
    private const string OpenConsoleTitle = "Open the web console in a browser";

    /// <summary>What changing the order is called.</summary>
    internal const string SortTitle = "Sort by name or by activity";

    /// <summary>What filing a conversation away is called.</summary>
    internal const string ArchiveTitle = "Archive this conversation";

    /// <summary>What bringing one back is called.</summary>
    internal const string UnarchiveTitle = "Put this conversation back in the list";

    /// <summary>What revealing the archived ones is called.</summary>
    internal const string ShowArchivedTitle = "Show archived conversations";

    /// <summary>What hiding them again is called.</summary>
    internal const string HideArchivedTitle = "Hide archived conversations";

    /// <summary>What the last action said, for the person and for a test.</summary>
    internal string Said { get; private set; } = string.Empty;

    /// <summary>Opens the dialog that makes a repository an agent of this project.</summary>
    /// <returns>When the dialog is open.</returns>
    internal Task Add() =>
        App is { } app && Projects.Selected is { } project
            ? Adding.OpenAsync(
                app,
                project,
                new Catalogue(ManifestPath),
                async (agent, token) => await BrokerDirectory.ReadAsync(async () =>
                    await new BrokerClient(_directory.ClientFor(project))
                        .StartAsync(agent, "it was just added", token).ConfigureAwait(false))
                    .ConfigureAwait(false),
                Queue,
                async said =>
            {
                Said = said;

                await SelectAsync(project, _reading.Token).ConfigureAwait(false);
            })
            : Task.CompletedTask;

    /// <summary>Opens the dialog that withdraws the selected agent.</summary>
    /// <returns>When the dialog is open.</returns>
    /// <remarks>
    /// The description is read before it is shown, and both reads already exist: the registry for
    /// what the agent is, and the waiting graph for what it owes. Asked here rather than kept from
    /// the last draw, because a debt is exactly the fact most likely to have changed since.
    /// </remarks>
    internal Task Withdraw()
    {
        if (App is not { } app || Projects.Selected is not { } project
            || Agents.Selected is not { } row)
        {
            return Task.CompletedTask;
        }

        Queue(async cancellationToken =>
        {
            var client = new BrokerClient(_directory.ClientFor(project));

            var registry = await BrokerDirectory.ReadAsync(async () =>
                await client.RegistryAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

            var waiting = await BrokerDirectory.ReadAsync(async () =>
                await client.WaitingAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

            if (!registry.Reachable || !waiting.Reachable)
            {
                Said = registry.Problem ?? waiting.Problem!;

                return;
            }

            var agent = registry.Value?.FirstOrDefault(a => a.Name.Value == row.Name);

            var owed = (waiting.Value ?? [])
                .Where(edge => edge.WaitedOn.Value == row.Name)
                .ToList();

            await Withdrawing.OpenAsync(
                app, client, row, agent, owed, project.NodeConfig, Queue, Reported(project))
                .ConfigureAwait(false);
        });

        return Task.CompletedTask;
    }

    /// <summary>Opens the dialog that reinstates the selected agent.</summary>
    /// <returns>When the dialog is open.</returns>
    internal Task Reinstate() =>
        App is { } app && Projects.Selected is { } project && Agents.Selected is { } row
            ? Withdrawing.OfferReinstate(
                app,
                new BrokerClient(_directory.ClientFor(project)),
                row,
                Queue,
                Reported(project))
            : Task.CompletedTask;

    /// <summary>Opens the dialog that takes a seat.</summary>
    /// <returns>When the dialog has been opened.</returns>
    internal Task Join()
    {
        if (App is not { } app || Seats is null || Projects.Selected is not { } project)
        {
            return Task.CompletedTask;
        }

        var dialog = Prompting.Text(
            AgentsRegion.JoinTitle,
            AgentsRegion.JoinQuestion,
            problem: AgentsRegion.NameProblem);

        var token = Prompting.Open(app, dialog);

        dialog.Done += (_, typed) =>
        {
            Prompting.Close(app, token);

            if (typed is null)
            {
                return;
            }

            Queue(async cancellationToken =>
            {
                var registry = await BrokerDirectory.ReadAsync(async () =>
                    await new BrokerClient(_directory.ClientFor(project))
                        .RegistryAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

                var (hold, said) = await AgentsRegion.JoinAsync(
                    Seats, project, registry.Value, typed, cancellationToken)
                    .ConfigureAwait(false);

                Held = hold ?? Held;
                Me = hold?.Seat ?? Me;
                Said = said;

                await SelectAsync(project, cancellationToken).ConfigureAwait(false);
            });
        };

        return Task.CompletedTask;
    }

    /// <summary>The person's own agent operations, for the project on screen.</summary>
    /// <returns>The voice, or null when they hold no seat here.</returns>
    /// <remarks>
    /// Built per use rather than held, because the seat and the project can each change under it.
    /// A voice cached across a project change would speak into the wrong deployment, which is the
    /// one mistake on this surface that cannot be taken back: the transcript would record it.
    /// </remarks>
    internal SeatVoice? Voice() =>
        Held is null || Projects.Selected is not { } project
            ? null
            : new SeatVoice(_directory.AgentsFor(project), Held.Seat);

    /// <summary>Takes delivery of what is waiting, after saying what that does.</summary>
    /// <returns>When the dialog has been opened.</returns>
    internal Task TakeDelivery()
    {
        if (App is not { } app || Held is null || Voice() is null)
        {
            return Task.CompletedTask;
        }

        var dialog = Prompting.Choose(
            Participation.DeliveryTitle,
            Participation.DeliveryWarning
            + Environment.NewLine
            + Environment.NewLine
            + string.Join(Environment.NewLine, Participation.Held(Held)),
            ["Not now", Participation.DeliveryTitle]);

        var token = Prompting.Open(app, dialog);

        dialog.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen != Participation.DeliveryTitle)
            {
                return;
            }

            Queue(async cancellationToken =>
            {
                if (Voice() is { } voice && Held is { } hold)
                {
                    Said = await Participation
                        .TakeDeliveryAsync(voice, hold, cancellationToken)
                        .ConfigureAwait(false);
                }
            });
        };

        return Task.CompletedTask;
    }

    /// <summary>Answers one of the questions this person owes.</summary>
    /// <returns>When the dialog has been opened.</returns>
    /// <remarks>
    /// The sub-question identifiers come from the broker rather than from the delivered payload.
    /// §5.3 answers by identifier and the store holds what was actually asked; a form built from a
    /// document this process parsed would be a second opinion about it, and the one that disagrees
    /// produces an answer resolving a sub-question nobody asked.
    /// </remarks>
    internal Task Answer()
    {
        if (App is not { } app || Held is not { } hold || Voice() is not { } voice)
        {
            return Task.CompletedTask;
        }

        var owed = hold.Delivered.Where(one => one.Owes).ToList();

        if (owed.Count == 0)
        {
            Said = "You owe nothing. Take delivery to see what is waiting.";

            return Task.CompletedTask;
        }

        var choosing = Prompting.Choose(
            Participation.AnswerTitle,
            "Which question?",
            [.. owed.Select(Participation.Label)]);

        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, picked) =>
        {
            Prompting.Close(app, token);

            var chosen = owed.Find(one => Participation.Label(one) == picked);

            if (chosen is null || chosen.Message.ExchangeId is not { } exchange)
            {
                Said = picked is null
                    ? string.Empty
                    : "That message is not part of an exchange to answer.";

                return;
            }

            Queue(cancellationToken => ResolveAsync(app, voice, hold, chosen, exchange, cancellationToken));
        };

        return Task.CompletedTask;
    }

    /// <summary>Asks a peer a question, opening a conversation.</summary>
    /// <returns>When the dialog has been opened.</returns>
    internal Task Ask() => Compose(asking: true);

    /// <summary>Tells a peer something, expecting no answer.</summary>
    /// <returns>When the dialog has been opened.</returns>
    internal Task Tell() => Compose(asking: false);

    /// <summary>Releases this person's seat, in every project at once.</summary>
    /// <returns>When it has been released.</returns>
    internal Task LeaveSeat()
    {
        if (Seats is null || Held is null)
        {
            return Task.CompletedTask;
        }

        Said = AgentsRegion.Leave(Seats, Held.Seat);
        Held = null;
        Me = null;

        if (Projects.Selected is { } project)
        {
            Queue(token => SelectAsync(project, token));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        // Every draw, because there is nowhere else this could hang: the surface has no menu to
        // revisit, and 004 swept on every visit to one. What it does not do is call this a touch.
        Tick(DateTimeOffset.UtcNow);

        return base.OnDrawingContent(context);
    }

    /// <summary>Records that somebody is actually here.</summary>
    /// <param name="at">When, or now when a caller has no reason to say.</param>
    /// <remarks>
    /// A key, a click, or an action taken. Drawing is not on the list, which is the whole point:
    /// an abandoned terminal goes on drawing, and a touch taken from a draw would keep its seat
    /// alive for ever. The instant is a parameter for the same reason <see cref="Tick"/>'s is —
    /// a test moves twenty minutes rather than waiting them.
    /// </remarks>
    internal void Touched(DateTimeOffset? at = null) => _touched = at ?? DateTimeOffset.UtcNow;

    /// <inheritdoc/>
    protected override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Taken here rather than left to the toolkit's own navigation, which walks every focusable
        // view including the list inside a region. What a person means by Tab on this surface is
        // "the next region", and a Tab that stepped into a list and then out of it again would
        // visit eight places to reach four.
        Touched();

        if (key == Key.Tab)
        {
            Move(forwards: true);

            return true;
        }

        if (key == Key.Tab.WithShift)
        {
            Move(forwards: false);

            return true;
        }

        if (key == QuitKey)
        {
            Quit();

            return true;
        }

        // **Escape goes back, and is swallowed at the top rather than passed on.** Taken before the
        // dialogs see it would be wrong -- a dialog's Escape closes the dialog -- but a dialog is a
        // session on top of this one and takes the key first, so by the time it arrives here there
        // is none open.
        //
        // **It used to return false at the top, and that was not the harmless thing it reads as.**
        // The comment said it left Escape "to whatever else wants it"; what wanted it was
        // Terminal.Gui, which binds `Esc => Quit` at application scope. So Escape on the projects
        // view ended the program -- which is exactly what the test named
        // `Escape_at_the_top_does_nothing_rather_than_anything_surprising` says must not happen,
        // and that test passed throughout because it asserted on the view rather than on whether a
        // stop had been requested. Nothing had ever pressed the key and looked.
        //
        // It matters more now than it did: the status line tells people to press Escape to go
        // back, so the keystroke that means "up one" reaches the top by design and one press too
        // many is an ordinary mistake rather than an odd one.
        if (key == Key.Esc)
        {
            if (_place.CanGoBack)
            {
                _ = BackAsync();
            }

            return true;
        }

        if (key == BrowserKey)
        {
            OpenConsole();

            return true;
        }

        // **Enter is taken here only on the projects view, and that asymmetry is deliberate.**
        // The conversations list raises its own event from the toolkit's `Accepting`, which is what
        // makes the key route and the mouse route one code path -- so intercepting Enter for every
        // view would take the key before that fires and break opening a conversation by pointer.
        // The projects list has no such handler, so there is nothing here to preempt.
        if (key == Key.Enter && _place.Viewing == Viewing.Projects)
        {
            _ = OpenAsync();

            return true;
        }

        // The letter shown beside an action in the status line takes it, when its region has focus.
        // Every one of them is also a menu entry or a button the mouse reaches: contracts/surface.md
        // is explicit that no action exists only as a key.
        if (Typed(key, ActionsKey))
        {
            _ = Offer();

            return true;
        }

        foreach (var action in Actions())
        {
            if (Typed(key, action.Key))
            {
                _ = action.Run();

                return true;
            }
        }

        return base.OnKeyDown(key);
    }

    /// <summary>Whether a keystroke is somebody typing a character this surface binds.</summary>
    /// <param name="key">What arrived.</param>
    /// <param name="character">The character an action is bound to.</param>
    /// <returns><see langword="true"/> when the two are the same keystroke.</returns>
    /// <remarks>
    /// <para>
    /// <b>The character, not the key code.</b> <c>?</c> is Shift and another key on every keyboard
    /// there is, and a driver is free to say so by carrying the shift modifier on what it hands
    /// up. Comparing against a bare <c>new Key('?')</c> then matches under one driver and not
    /// another — the same keystroke, the same code, working on the machine the suite runs on and
    /// dead on somebody's terminal. That is what happened, and it is invisible from inside a test
    /// that injects the key it is about to compare against.
    /// </para>
    /// <para>
    /// Control and Alt are excluded rather than ignored. They are how a terminal spells a
    /// different keystroke entirely, and an action that answered to Ctrl+C as readily as to
    /// <c>c</c> would fire on the chord half the world presses to stop a program.
    /// </para>
    /// </remarks>
    private static bool Typed(Key key, char character) =>
        !key.IsCtrl && !key.IsAlt && key.AsRune == new Rune(character);

    /// <summary>Reads the manifest and every broker, and draws the projects.</summary>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>When the projects region has been drawn.</returns>
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var projects = _directory.Projects;
        var health = await HealthAsync(projects, cancellationToken).ConfigureAwait(false);

        Projects.Show(projects, health, _directory.ManifestProblem);

        if (Projects.Selected is { } selected)
        {
            await SelectAsync(selected, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads one project and draws its agents and conversations.</summary>
    /// <param name="project">The project to read.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>When both regions have been drawn.</returns>
    /// <remarks>
    /// The two reads are contained separately. A registry that answers and a conversation list
    /// that does not is a real state — the broker restarting between two calls — and a region
    /// that showed nothing because its neighbour failed would be hiding what it does know.
    /// </remarks>
    private async Task SelectAsync(ProjectSettings project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        var client = new BrokerClient(_directory.ClientFor(project));

        var registry = await BrokerDirectory.ReadAsync(async () =>
            await client.RegistryAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        var conversations = await BrokerDirectory.ReadAsync(async () =>
            await client.ConversationsAsync(
                includeArchived: _showArchived, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            .ConfigureAwait(false);

        // Kept even when the read failed, so the count on screen is not quietly reset to zero by a
        // broker that stopped answering: a surface saying "no archived conversations" because it
        // could not ask is saying something it does not know.
        if (conversations.Value is { } page)
        {
            _archivedOmitted = page.ArchivedOmitted;
        }

        Agents.Show(registry.Value, Catalogue(project), registry.Problem);
        Conversations.Show(
            Ordered(conversations.Value?.Items), conversations.Problem, _archivedOmitted, _showArchived);
        Transcript.Clear();
    }

    /// <summary>Puts a page of conversations in the order the person asked for.</summary>
    /// <param name="page">What the broker returned, or null when it could not be asked.</param>
    /// <returns>The conversations, ordered.</returns>
    /// <remarks>
    /// Sorted here rather than by the broker, because it is a preference about looking. Asking the
    /// broker to re-page in a different order every time somebody pressed a key would make a view
    /// preference cost a round trip and, worse, make two orderings two different pages.
    /// </remarks>
    private IReadOnlyList<ConversationSummary>? Ordered(IReadOnlyList<ConversationSummary>? page)
    {
        if (page is null)
        {
            return null;
        }

        var rows = ConversationsView.Sorted(ConversationsView.Rows(page), _sort);

        return [.. rows.Select(row => page.First(c => c.Id == row.Id))];
    }

    /// <summary>Changes how the conversations are ordered, and redraws.</summary>
    /// <returns>When the list has been redrawn.</returns>
    private Task SortConversations()
    {
        _sort = _sort == SortBy.Activity ? SortBy.Name : SortBy.Activity;
        Said = _sort == SortBy.Name ? "Sorted by name." : "Sorted by most recent activity.";

        Queue(token => Current is { } project ? SelectAsync(project, token) : Task.CompletedTask);

        return Task.CompletedTask;
    }

    /// <summary>Shows or hides the archived conversations, and redraws.</summary>
    /// <returns>When the list has been redrawn.</returns>
    private Task ToggleArchived()
    {
        _showArchived = !_showArchived;
        Said = _showArchived ? "Showing archived conversations." : "Hiding archived conversations.";

        Queue(token => Current is { } project ? SelectAsync(project, token) : Task.CompletedTask);

        return Task.CompletedTask;
    }

    /// <summary>The project the current view is about.</summary>
    /// <remarks>
    /// <b>The view's project, falling back to the list's selection.</b> They are the same until the
    /// projects list is off screen, which is exactly when every action inside a project runs -- so
    /// reading the list made those actions depend on a region nobody is looking at, and on a
    /// region that may never have been filled at all.
    /// </remarks>
    private ProjectSettings? Current =>
        _place.Project is { } named ? _directory.Find(named.Value) : Projects.Selected;

    /// <summary>Files the selected conversation away, or brings it back.</summary>
    /// <returns>When the change has been asked for.</returns>
    /// <remarks>
    /// <b>No typed name, and that is the line 004 drew rather than an oversight.</b> Nothing is
    /// destroyed and one keystroke reverses it, so a door built for a purge would be a door in
    /// front of a light switch.
    /// </remarks>
    private Task ArchiveSelected()
    {
        if (Conversations.Selected is not { } conversation || Current is not { } project)
        {
            return Task.CompletedTask;
        }

        var archived = conversation.Archived;

        Queue(async token =>
        {
            var client = new BrokerClient(_directory.ClientFor(project));

            var changed = await BrokerDirectory.ReadAsync(async () => archived
                ? await client.UnarchiveAsync(conversation.Id, token).ConfigureAwait(false)
                : await client.ArchiveAsync(conversation.Id, token).ConfigureAwait(false))
                .ConfigureAwait(false);

            Said = !changed.Reachable
                ? changed.Problem!
                : changed.Value
                    ? archived
                        ? $"{Safe.Text(conversation.Id)} is back in the list."
                        : $"{Safe.Text(conversation.Id)} is archived."
                    : "Nothing changed.";

            await SelectAsync(project, token).ConfigureAwait(false);
        });

        return Task.CompletedTask;
    }

    /// <summary>Opens the dialog that destroys the selected conversation.</summary>
    /// <returns>When the dialog is open.</returns>
    internal Task DestroyConversation() => DestroySelected();

    /// <summary>Opens the dialog that destroys the selected conversation.</summary>
    /// <returns>When the dialog is open.</returns>
    private Task DestroySelected()
    {
        if (App is not { } app
            || Conversations.Selected is not { } conversation
            || Current is not { } project)
        {
            return Task.CompletedTask;
        }

        var row = ConversationsView.Rows([conversation])[0];
        var client = new BrokerClient(_directory.ClientFor(project));

        return Destroying.OpenAsync(app, client, row, Queue, async said =>
        {
            Said = said;

            await SelectAsync(project, _reading.Token).ConfigureAwait(false);
        });
    }

    /// <summary>Reads one conversation and draws it.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="conversationId">Which conversation.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>When the transcript region has been drawn.</returns>
    internal async Task OpenAsync(
        ProjectSettings project,
        string conversationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(_directory.ClientFor(project))
                .ExportAsync(conversationId, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        Transcript.Show(conversationId, project.Name.Value, read.Value, read.Problem);
    }

    /// <summary>
    /// Reads the project's catalogue, which is the one fact about a deployment the broker never
    /// sees.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <returns>What this machine could start for it, or an empty catalogue.</returns>
    /// <remarks>
    /// Read fresh on every draw rather than held. Somebody may edit it by hand while this is open,
    /// and an agents region showing a catalogue from a minute ago would report an agent that is no
    /// longer startable as though it were.
    /// </remarks>
    private static NodeCatalogue Catalogue(ProjectSettings project) =>
        project.NodeConfig is { Length: > 0 } path
            && NodeCatalogue.TryReadFrom(path, out var catalogue, out _)
            ? catalogue
            : NodeCatalogue.Empty;

    /// <summary>Asks every project's broker what it speaks, all at once.</summary>
    /// <param name="projects">The projects to ask.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>What each said, by project name.</returns>
    private async Task<IReadOnlyDictionary<string, ProjectHealth>> HealthAsync(
        IReadOnlyList<ProjectSettings> projects,
        CancellationToken cancellationToken)
    {
        var asked = projects.Select(async project =>
        {
            var read = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(_directory.ClientFor(project))
                    .ImplementsAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

            return (project.Name.Value, Health: new ProjectHealth(read.Value, read.Problem));
        });

        var answers = await Task.WhenAll(asked).ConfigureAwait(false);

        return answers.ToDictionary(a => a.Value, a => a.Health, StringComparer.Ordinal);
    }

    /// <summary>Collects an answer per sub-question, then sends them together.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="voice">The person's own agent operations.</param>
    /// <param name="hold">The seat holding the question.</param>
    /// <param name="owed">The delivered question.</param>
    /// <param name="exchange">The exchange it belongs to.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>When the answer has been sent, or abandoned.</returns>
    /// <remarks>
    /// One form for the whole exchange rather than a dialog per sub-question: a person answering
    /// the third cannot otherwise revise the first, which is exactly when they notice the first was
    /// answering something narrower than was asked. The outcome for each is chosen from
    /// <c>SeatVoice.Outcomes</c>, so <c>ghosted</c> can never appear among them.
    /// </remarks>
    private async Task ResolveAsync(
        IApplication app,
        SeatVoice voice,
        SeatHold hold,
        Delivered owed,
        long exchange,
        CancellationToken cancellationToken)
    {
        var ids = await voice.SubQuestionsAsync(exchange, cancellationToken).ConfigureAwait(false);

        if (ids.Count == 0)
        {
            Said = "The broker says that exchange asked nothing to resolve.";

            return;
        }

        var fields = new List<FormField>();

        foreach (var id in ids)
        {
            fields.Add(new FormField(
                $"{id} — how does it resolve? ({string.Join(" / ", SeatVoice.Outcomes.Select(o => o.Value))})",
                SeatVoice.Outcomes[0].Value,
                Outcome));

            fields.Add(new FormField($"{id} — your answer:", string.Empty, Participation.AnswerProblem));
        }

        var form = new FormDialog(
            Participation.AnswerTitle + Environment.NewLine + Participation.Asked(owed),
            fields);

        var token = Prompting.Open(app, form);

        form.Done += (_, values) =>
        {
            Prompting.Close(app, token);

            if (values is null)
            {
                return;
            }

            var resolutions = ids
                .Select((id, at) => (Id: id, Text: values[(at * 2) + 1], Outcome: values[at * 2].Trim()))
                .ToList();

            Queue(async token2 =>
            {
                // Through the overload that settles the seat as well, so this surface never strikes
                // a question off its own list while the broker still believes it open.
                var answered = await voice
                    .AnswerAsync(hold, exchange, resolutions, token2)
                    .ConfigureAwait(false);

                Said = answered.Says;

                if (Projects.Selected is { } project)
                {
                    await SelectAsync(project, token2).ConfigureAwait(false);
                }
            });
        };

        static string? Outcome(string typed) =>
            SeatVoice.Outcomes.Any(o => string.Equals(o.Value, typed.Trim(), StringComparison.Ordinal))
                ? null
                : $"One of: {string.Join(", ", SeatVoice.Outcomes.Select(o => o.Value))}";
    }

    /// <summary>Opens the form that asks a peer a question, or tells them something.</summary>
    /// <param name="asking">Whether an answer is expected.</param>
    /// <returns>When the dialog has been opened.</returns>
    /// <remarks>
    /// The fields are collected in the order rule 8.1 puts them — what is being decided, then the
    /// question, then what turns on the answer. Asking for the question first invites somebody to
    /// write it and stop.
    /// </remarks>
    private Task Compose(bool asking)
    {
        if (App is not { } app || Voice() is not { } voice || Projects.Selected is not { } project)
        {
            return Task.CompletedTask;
        }

        var fields = asking
            ?
            [
                new FormField("Peer:", string.Empty, Participation.RequiredProblem),
                new FormField("What are you trying to decide?", string.Empty, Participation.RequiredProblem),
                new FormField("The question:", string.Empty, Participation.RequiredProblem),
                new FormField("What turns on the answer?", string.Empty, Participation.ConsequencesProblem),
                new FormField("Conversation name:", Participation.Suggested(), Participation.RequiredProblem),
            ]
            : new List<FormField>
            {
                new("Peer:", string.Empty, Participation.RequiredProblem),
                new("What are you telling them?", string.Empty, Participation.RequiredProblem),
                new(
                    "Conversation name:",
                    Transcript.Open ?? Participation.Suggested(),
                    Participation.RequiredProblem),
            };

        var form = new FormDialog(asking ? Participation.AskTitle : Participation.TellTitle, fields);
        var token = Prompting.Open(app, form);

        form.Done += (_, values) =>
        {
            Prompting.Close(app, token);

            if (values is null || !AgentName.TryCreate(values[0].Trim(), out var peer))
            {
                Said = values is null ? string.Empty : $"'{values[0]}' is not an agent name.";

                return;
            }

            Queue(async cancellationToken =>
            {
                Said = asking
                    ? await voice.AskAsync(
                        peer, values[1], values[2], values[3], values[4], cancellationToken)
                        .ConfigureAwait(false)
                    : await voice.NotifyAsync(peer, values[1], values[2], cancellationToken)
                        .ConfigureAwait(false);

                await SelectAsync(project, cancellationToken).ConfigureAwait(false);
            });
        };

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        // **Guarded, because being disposed twice is the ordinary case rather than the odd one.**
        // The application disposes the view it was running when its session ends, and the caller
        // holds it in a `using` as well — so an unguarded Cancel on an already-disposed source
        // throws while somebody is quitting, which is the least recoverable moment there is.
        if (disposing && !_closed)
        {
            _closed = true;

            // Cancelled before the base disposes the views, so a read still in flight cannot draw
            // into a region that has gone. The seat book's streams are not this object's to end;
            // TopCommand owns those and ends them when the process does.
            _reading.Cancel();
            _reading.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>A region's title, which is also what the status line calls it.</summary>
    /// <param name="region">The region.</param>
    /// <returns>Its title.</returns>
    private static string NameOf(View region) => region.Title;

    /// <summary>The manifest to write into, saying so when there is none.</summary>
    /// <returns>The path, or null when this surface was given its projects.</returns>
    private string? Manifest()
    {
        if (ManifestPath is { Length: > 0 } path)
        {
            return path;
        }

        Said = "This surface was given its projects, so there is no manifest here to write to.";

        return null;
    }

    /// <summary>Takes what a screen said and shows it, then reads the project again.</summary>
    /// <param name="project">The project to read again.</param>
    /// <returns>What to hand a screen that reports when it is done.</returns>
    /// <remarks>
    /// The re-read is the point: an agent that has just been retired must not still be drawn as
    /// running, and the row is a rendering of the registry rather than a copy of it.
    /// </remarks>
    private Func<string, Task> Reported(ProjectSettings project) =>
        async said =>
        {
            Said = said;

            await SelectAsync(project, _reading.Token).ConfigureAwait(false);
        };

    /// <summary>Takes what a screen said and shows it, then reads the projects again.</summary>
    /// <param name="said">What the command wrote.</param>
    /// <returns>When the projects have been read again.</returns>
    private async Task Reporting(string said)
    {
        Said = said;

        await RefreshAsync(_reading.Token).ConfigureAwait(false);
    }

    /// <summary>Runs a read after whatever is already running, and remembers it.</summary>
    /// <param name="read">The read to run.</param>
    /// <remarks>
    /// <para>
    /// Chained rather than run at once, and that is the fix rather than the tidy version. Two
    /// reads racing means the slower one draws last: hold an arrow key down the projects list and
    /// the agents region ends up showing whichever project's broker happened to answer last, which
    /// is not the one the highlight is on. Ordering them means the last key pressed is the last
    /// thing drawn.
    /// </para>
    /// <para>
    /// A read that faults is swallowed here on purpose. Every broker read is already contained by
    /// <see cref="BrokerDirectory.ReadAsync{T}"/>, so what could reach this is a defect rather
    /// than an unreachable broker — and taking the surface down over one region's redraw is the
    /// failure a person can do least about.
    /// </para>
    /// <para>
    /// <b>Extending the chain is one step, under a lock, and reading it is the same step.</b>
    /// Reading the field, chaining off it and writing it back is three, and two callers arriving
    /// together both chain off the same link: the work still runs, so nothing is lost and a count
    /// taken afterwards proves nothing, but it runs <i>beside</i> the work that displaced it rather
    /// than after it, and <see cref="Settled"/> stops covering it. That is both halves of what this
    /// exists for gone at once — the ordering a person sees, and the determinism every driven test
    /// in this suite rests on. Measured rather than reasoned about: sixty-four callers at once
    /// overlapped twenty-three times and left forty-five outside the task handed back.
    /// </para>
    /// </remarks>
    private void Queue(Func<CancellationToken, Task> read)
    {
        lock (_chain)
        {
            _work = Chain(read);
        }
    }

    /// <summary>The next link, built from the chain the caller already holds the lock on.</summary>
    private Task Chain(Func<CancellationToken, Task> read) =>
        _work.ContinueWith(
            async _ =>
            {
                try
                {
                    await read(_reading.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The surface is going away. Not news, and saying so would put "the operation
                    // was cancelled" on screen every time somebody quit.
                }
                catch (Exception failure)
                {
                    // **Everything, and it is said rather than swallowed.** An earlier version
                    // caught only the cancellation while its own comment claimed it contained
                    // faults, which is the worst of both: the chain's task faulted, nothing
                    // observed it, and the next `await Settled` rethrew somebody else's exception
                    // long after the thing that caused it.
                    //
                    // Every broker read is already contained by BrokerDirectory.ReadAsync, so what
                    // reaches here is a defect — and the right answer to a defect in one region's
                    // redraw is a sentence on the status line, not the end of a session that is
                    // showing three other projects.
                    Said = failure.Message;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();

    /// <summary>Queues work that throws, so a test can prove the containment above.</summary>
    /// <param name="failure">What to throw.</param>
    /// <remarks>
    /// Internal and test-only, and deliberately not a flag on a real read: the property being
    /// checked is what the queue does with an exception, and reaching it through a broker that
    /// happened to misbehave would test the broker instead.
    /// </remarks>
    internal void Failing(Exception failure) => Queue(_ => Task.FromException(failure));

    /// <summary>Queues arbitrary work, so a test can prove the ordering above.</summary>
    /// <param name="work">What to run.</param>
    /// <remarks>
    /// Internal and test-only, for the reason <see cref="Failing"/> is: the property is what the
    /// queue does with several pieces of work, and reaching it through brokers that happened to
    /// answer at different speeds would be testing the brokers.
    /// </remarks>
    internal void Queued(Func<CancellationToken, Task> work) => Queue(work);
}

/// <summary>One thing the focused region can do, and the letter that does it.</summary>
/// <param name="Key">The letter shown in the status line.</param>
/// <param name="Label">What it is called, in the same words the dialog uses.</param>
/// <param name="Run">Doing it.</param>
/// <remarks>
/// A record rather than a method per action so that the status line and the key handler read the
/// same list. Two copies of "what can be done here" is how a surface comes to offer something no
/// key takes, or take something nothing offered.
/// </remarks>
internal sealed record RegionAction(char Key, string Label, Func<Task> Run);
