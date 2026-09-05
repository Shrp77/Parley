using Parley.Client;
using Parley.Client.Seating;
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
    /// Eighty columns, and a test drives seventy-nine and eighty rather than trusting it. The
    /// number is a constant rather than a calculation because a layout that rearranged itself at
    /// a width nobody could name would be untestable at its own boundary.
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

    private readonly BrokerDirectory _directory;
    private readonly Label _status = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
    private readonly List<View> _regions;
    private readonly CancellationTokenSource _reading = new();

    private bool _stacked;
    private bool _closed;
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
        Conversations.Opened += (_, conversation) => Queue(token =>
            Projects.Selected is { } project
                ? OpenAsync(project, conversation.Id, token)
                : Task.CompletedTask);

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

    /// <summary>Which region has focus.</summary>
    internal int FocusedRegion { get; private set; }

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
    internal Task Settled => _work;

    /// <summary>How long a read started by a keystroke may take before it is abandoned.</summary>
    /// <remarks>
    /// Only relevant to the token this hands its own reads. A broker that never answers is already
    /// contained by <see cref="BrokerDirectory.ReadAsync{T}"/>; this exists so a surface being torn
    /// down does not wait on one.
    /// </remarks>
    internal CancellationToken Reading => _reading.Token;

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

        foreach (var region in _regions)
        {
            region.SuperView?.Remove(region);
        }

        if (stacked)
        {
            // One at a time, each filling the width, rather than pages of a tab control.
            //
            // **The toolkit's tab strip was tried first and is what the specification forbids.**
            // Four titles across the top of a forty-column terminal do not fit, and what the strip
            // does about that is cut them: `Conversations` became `Conversat`, silently. Running
            // the strip down the side writes each title one letter per row, which is worse. So the
            // region fills the space, its own frame carries its title in full, and the status line
            // says which of the four it is and how to reach the rest.
            foreach (var region in _regions)
            {
                region.X = 0;
                region.Y = 0;
                region.Width = Dim.Fill();
                region.Height = Dim.Fill(1);
            }

            Add(Projects, Agents, Conversations, Transcript);
        }
        else
        {
            // Projects on the left, agents over conversations in the middle, the transcript on
            // the right. The transcript is widest because it is the only region holding prose.
            Projects.X = 0;
            Projects.Y = 0;
            Projects.Width = Dim.Percent(25);
            Projects.Height = Dim.Fill(1);

            Agents.X = Pos.Right(Projects);
            Agents.Y = 0;
            Agents.Width = Dim.Percent(35);
            Agents.Height = Dim.Percent(50);

            Conversations.X = Pos.Right(Projects);
            Conversations.Y = Pos.Bottom(Agents);
            Conversations.Width = Dim.Percent(35);
            Conversations.Height = Dim.Fill(1);

            Transcript.X = Pos.Right(Agents);
            Transcript.Y = 0;
            Transcript.Width = Dim.Fill();
            Transcript.Height = Dim.Fill(1);

            // **Made visible again, because the stacked layout hides three of them.** Going narrow
            // and coming back is one drag of a window edge, and without this the surface returns
            // showing one region and three gaps -- which reads as three regions lost to the resize
            // rather than as a layout that forgot to undo itself.
            foreach (var region in _regions)
            {
                region.Visible = true;
            }

            Add(Projects, Agents, Conversations, Transcript);
        }

        Focus(FocusedRegion);
    }

    /// <summary>Moves focus to a region and says so.</summary>
    /// <param name="at">Which region, counted from the projects.</param>
    internal void Focus(int at)
    {
        FocusedRegion = ((at % _regions.Count) + _regions.Count) % _regions.Count;

        var region = _regions[FocusedRegion];

        if (_stacked)
        {
            foreach (var other in _regions)
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

        var where = _stacked
            ? $"{FocusPrefix}{NameOf(region)} ({FocusedRegion + 1}/{_regions.Count}, Tab)"
            : FocusPrefix + NameOf(region);

        var offered = actions.Count == 0
            ? string.Empty
            : "  " + string.Join("  ", actions.Select(a => $"{a.Key}: {a.Label}"));

        // **Listed in full when they fit, and behind one key when they do not.** Six labelled
        // actions are a hundred and forty columns; a status line that simply held them would be
        // cut by the toolkit at whatever column the terminal ends at, which is the silent
        // truncation the narrow layout exists to avoid, arrived at from the other direction. What
        // replaces them is not a shorter list — it is the way to see the whole one.
        var room = Viewport.Width > 0 ? Viewport.Width : Threshold;

        _status.Text = where.Length + offered.Length <= room
            ? where + offered
            : where + (actions.Count == 0 ? string.Empty : $"  {ActionsKey}: actions");
    }

    /// <summary>Moves focus to the next region, or the previous one.</summary>
    /// <param name="forwards">Whether to go forwards.</param>
    internal void Move(bool forwards) => Focus(FocusedRegion + (forwards ? 1 : -1));

    /// <summary>What the focused region can do, and the letter that does each.</summary>
    /// <returns>The actions, in the order the status line lists them.</returns>
    /// <remarks>
    /// Actions belong to the region that shows the thing acted on: a seat is taken from the agents
    /// region, a question is answered from a transcript, a project is created or removed from the
    /// projects region. That is User Story 2's rule, and having the region answer for its own
    /// actions is what keeps the status line and the keys from drifting apart.
    /// </remarks>
    internal IReadOnlyList<RegionAction> Actions() => _regions[FocusedRegion] switch
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

        if (App is not { } app || actions.Count == 0)
        {
            return Task.CompletedTask;
        }

        var menu = Prompting.Choose(
            ActionsTitle,
            NameOf(_regions[FocusedRegion]),
            [.. actions.Select(action => $"{action.Key}: {action.Label}")]);

        var token = Prompting.Open(app, menu);

        menu.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (actions.FirstOrDefault(a => $"{a.Key}: {a.Label}" == chosen) is { } taken)
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
    internal Task OpenConsole()
    {
        Launch.Open(said => Said = said, ConsoleAddress);

        return Task.CompletedTask;
    }

    /// <summary>Where the web console listens when a broker hosts it.</summary>
    internal const string ConsoleAddress = "http://127.0.0.1:5045";

    /// <summary>What leaving a seat is called.</summary>
    internal const string LeaveSeatTitle = "Leave your seat";

    /// <summary>What following the broker's log is called.</summary>
    internal const string FollowLogTitle = "Follow the broker log";

    /// <summary>What opening the web console is called.</summary>
    internal const string OpenConsoleTitle = "Open the web console in a browser";

    /// <summary>What the last action said, for the person and for a test.</summary>
    internal string Said { get; private set; } = string.Empty;

    /// <summary>Opens the dialog that makes a repository an agent of this project.</summary>
    /// <returns>When the dialog is open.</returns>
    internal Task Add() =>
        App is { } app && Projects.Selected is { } project
            ? Adding.OpenAsync(app, project, new Catalogue(ManifestPath), Queue, async said =>
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

        // The letter shown beside an action in the status line takes it, when its region has focus.
        // Every one of them is also a menu entry or a button the mouse reaches: contracts/surface.md
        // is explicit that no action exists only as a key.
        if (key == new Key(ActionsKey))
        {
            _ = Offer();

            return true;
        }

        foreach (var action in Actions())
        {
            if (key == new Key(action.Key))
            {
                _ = action.Run();

                return true;
            }
        }

        return base.OnKeyDown(key);
    }

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
    internal async Task SelectAsync(ProjectSettings project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        var client = new BrokerClient(_directory.ClientFor(project));

        var registry = await BrokerDirectory.ReadAsync(async () =>
            await client.RegistryAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        var conversations = await BrokerDirectory.ReadAsync(async () =>
            await client.ConversationsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        Agents.Show(registry.Value, Catalogue(project), registry.Problem);
        Conversations.Show(conversations.Value?.Items, conversations.Problem);
        Transcript.Clear();
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
    /// </remarks>
    private void Queue(Func<CancellationToken, Task> read) =>
        _work = _work.ContinueWith(
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
