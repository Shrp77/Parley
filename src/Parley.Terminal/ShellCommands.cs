using Parley.Client;
using Parley.Client.Rendering;
using Parley.Client.Views;
using Terminal.Gui.Input;

namespace Parley.Terminal;

/// <summary>Every command the surface offers, where each is live, and the keys the surface keeps for itself.</summary>
/// <remarks>
/// <para>
/// <b>Declared here rather than built per keystroke</b>, so <see cref="CommandManager{TTarget}"/> can refuse a clash when the
/// surface is constructed. A command belongs to the place that shows the thing it acts on: a seat is taken from the
/// agents pane, a question is answered from a transcript, a project is created or removed from the projects list.
/// </para>
/// <para>
/// <b>Order within a place is the order the status line lists them</b>, and the conditions are the ones each offer
/// always had: offered only while it would do something, so the line never names a key that does nothing.
/// </para>
/// </remarks>
internal sealed partial class Shell
{
    /// <summary>Every place and the layers live in it.</summary>
    /// <remarks>
    /// <b>Both layers are live in every place.</b> The message line and what belongs to no pane are on every screen,
    /// so their keys are checked against every pane's, including a pane that is shown only once a roadmap exists.
    /// </remarks>
    internal static IReadOnlyList<KeyPlace> Places =>
    [
        .. new[]
        {
            KeyContext.Projects, KeyContext.Agents, KeyContext.Conversations, KeyContext.Transcript, KeyContext.Room,
            KeyContext.Line, KeyContext.Plans, KeyContext.Roadmaps, KeyContext.Configuration, KeyContext.Elsewhere,
        }.Select(context => new KeyPlace(context, [KeyContext.MessageLine, KeyContext.Everywhere])),
    ];

    /// <summary>What a list's own keys are called when a clash names one.</summary>
    internal const string ListName = "the list's cursor";

    /// <summary>The panes that are a list, and so take a list's keys.</summary>
    private static readonly KeyContext[] ListPlaces =
    [
        KeyContext.Projects, KeyContext.Agents, KeyContext.Conversations, KeyContext.Transcript, KeyContext.Room,
        KeyContext.Plans, KeyContext.Roadmaps, KeyContext.Configuration,
    ];

    /// <summary>Every key taken before a command is asked: by <c>OnKeyDown</c>, or by the view that has focus in a pane.</summary>
    /// <remarks>
    /// <para>
    /// <b>A focused view is offered a key before the surface is</b>, so a key a pane's list or the box takes never
    /// reaches a command. Each place therefore reserves what its own views take: every pane but the box is a list, which
    /// moves its cursor on the arrows, the page keys and their chords; the box edits on Ctrl and the editing keys.
    /// <c>KeysTakenBeforeTheTableTests</c> builds each pane and presses every key at it, so a view that takes a key its
    /// place does not claim fails a unit test.
    /// </para>
    /// <para>
    /// Escape is not here: going back is a command, in <see cref="KeyContext.Everywhere"/>, and that is what claims it.
    /// Enter on the agents list and on the roadmap lists is a command too. The Enters here are the ones a pane takes with
    /// no label of its own: the conversations and the room take it through their list's own accept, which is why it was
    /// missed when only <c>OnKeyDown</c> was read.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<ReservedKey> Reserved =>
    [
        new(KeyContext.Everywhere, Key.Tab, "the next pane"),
        new(KeyContext.Everywhere, Key.Tab.WithShift, "the previous pane"),
        new(KeyContext.Everywhere, QuitKey, QuitLabel),
        new(KeyContext.Everywhere, BrowserKey, OpenConsoleTitle),
        new(KeyContext.Everywhere, MenuKey, "the menu bar"),
        new(KeyContext.Projects, Key.Enter, "open the project"),
        new(KeyContext.Conversations, Key.Enter, "open the conversation"),
        new(KeyContext.Transcript, Key.Enter, "open the folded message"),
        new(KeyContext.Room, Key.Enter, "start, write to or read the row"),
        new(KeyContext.Configuration, Key.Enter, ConfigurationRegion.ChangeTitle),

        // The box binds the arrows too; while a completion list is offered the surface moves its selection instead.
        new(KeyContext.Line, Key.CursorUp, "the box's cursor, or the completion list"),
        new(KeyContext.Line, Key.CursorDown, "the box's cursor, or the completion list"),

        // The box takes Ctrl+Enter itself, as the line break Shift+Enter is: several terminals cannot send the one.
        new(KeyContext.Line, Key.Enter.WithCtrl, BreakTitle),

        .. ListPlaces.SelectMany(place => ReservedKey.TakenBy(place, () => Lists.Filling(), ListName)),

        // Enter is sending, which the box takes on its own key handler and the command names.
        .. ReservedKey.TakenBy(KeyContext.Line, () => Entry.Speaking().View, "the box", Key.Enter),
        .. ReservedKey.Typing(KeyContext.Line, "the box"),
    ];

    /// <summary>Every command, in every place and layer.</summary>
    internal static IReadOnlyList<KeyCommand<Shell>> Registered =>
    [
        // **The projects list.** Auto Accept lives in the configuration view, reached from Everywhere, which says what
        // it decides before anybody changes it.
        new(KeyContext.Projects, 'c', Naming.CreateTitle, s => s.Create(), KeyCommand.Always),
        new(KeyContext.Projects, 'r', Removing.Title, s => s.Remove(), KeyCommand.Always),

        // **Three of these belong to the deployment however they are reached**: health is read across every project,
        // the log is the broker's, and the web UI is another way into the whole thing. They are offered from this
        // place because this is where somebody is standing, and that is not the same as what they are about.
        new(KeyContext.Projects, 'y', HealthTitle, s => s.ShowHealth(), KeyCommand.Always, Category: ActionCategory.Deployment),
        new(KeyContext.Projects, 'l', Files.ProjectTitle, s => s.ShowFiles(), KeyCommand.Always),
        new(KeyContext.Projects, 'p', Pruning.Title, s => s.PruneSelected(), KeyCommand.Always),
        new(KeyContext.Projects, 'g', FollowLogTitle, s => s.FollowLog(), KeyCommand.Always, Category: ActionCategory.Deployment),
        new(KeyContext.Projects, 'b', OpenConsoleTitle, s => s.OpenConsole(), KeyCommand.Always, Category: ActionCategory.Deployment),

        // **The agents list.** Enter's meaning here, named where the others are: a key the surface takes and does not
        // name is one nobody finds. Offered only on a row, since that is what it acts on.
        new(KeyContext.Agents, default, RowMenuTitle, s => s.RowMenu(), s => s.Agents.Selected is not null, Chord: Key.Enter),
        new(KeyContext.Agents, 'a', Adding.Title, s => s.Add(), KeyCommand.Always),

        // Beside adding one, because it is the same act for many: the two ways a project gains agents belong on one
        // menu. `f` for the folder it asks for.
        new(KeyContext.Agents, 'f', Onboarding.Title, s => s.Enrol(), KeyCommand.Always),

        // Offered only while there is something to start, so the line never names a key that would do nothing — and,
        // on a deployment where every agent has registered, never appears at all.
        new(
            KeyContext.Agents,
            'u',
            StartEveryTitle(0),
            s => s.StartEvery(),
            s => s.NeverRegistered().Count > 0,
            Says: s => StartEveryTitle(s.NeverRegistered().Count)),

        // Never for a seat, whoever holds it: a person leaves a seat, they are not retired (research R7). Withdrawing
        // refuses one reached by any other route as well.
        new(
            KeyContext.Agents,
            'i',
            Withdrawing.ReinstateTitle,
            s => s.Reinstate(),
            s => s.Agents.Selected is { Seat: false } chosen && chosen.State.Contains("retired", StringComparison.Ordinal)),
        new(
            KeyContext.Agents,
            'r',
            Withdrawing.Title,
            s => s.Withdraw(),
            s => s.Agents.Selected is { Seat: false } chosen && !chosen.State.Contains("retired", StringComparison.Ordinal)),

        // **One page for what an agent is responsible for**: what it has said, the line in a field to change, and
        // asking it to refresh. Never for a seat: an ownership line is how peers decide who to send work to, and a
        // person is not routed to by one. Needs no seat, because Parley asks and a person's line is recorded as theirs.
        new(
            KeyContext.Agents,
            'n',
            Responsibility.Title,
            s => s.ResponsibilityOfSelected(),
            s => s.Agents.Selected is { Seat: false },
            Owing.Requesting),

        // **Offered while there is anybody to ask, and named with the count**, because it launches one exchange per
        // agent and a person deciding is deciding about that number. Offered without a seat for the reason the single
        // ask is: an action nobody can find is indistinguishable from one that does not exist.
        new(
            KeyContext.Agents,
            'e',
            Ownership.AskEveryTitle,
            s => s.AskEveryoneWhatTheyOwn(),
            s => s.Agents.Registry.Count > 0,
            Owing.Requesting),

        // **The review is what makes Auto Accept safe to leave on**: every line, where it came from, and whatever
        // Parley is waiting on, with the decisions about each. `v`, as in review; needing no seat, because nothing in
        // it speaks as the person.
        new(KeyContext.Agents, 'v', OwnershipReview.Title, s => s.ReviewOwnership(), s => s.Agents.Registry.Count > 0),

        // **No join and no leave**. The broker provides the person's seat in every project and this terminal takes it
        // on its own; closing the terminal is how a person leaves.

        // **The room column.** Space on a participant includes or excludes it, and on an agent that has never run
        // starts it: one key, so one command, named for the row it is on. A key named where it does nothing is the
        // fault this surface keeps a rule about, so it is offered only on a row it would act on.
        new(
            KeyContext.Room,
            default,
            IncludeTitle,
            s => s.ToggleOne(),
            s => s.Room.SelectedUnstarted is not null || (s.Held is not null && s.Room.SelectedPeer is not null),
            Chord: Key.Space,
            Says: s => s.Room.SelectedUnstarted is not null ? StartFromRoomTitle : IncludeTitle),

        // **Confirmed roadmaps.** Enter on one opens it; named, since a key the surface takes and does not name is one
        // nobody finds.
        new(KeyContext.Plans, default, PlansRegion.OpenTitle, s => s.OpenSelectedPlan(), s => s.Plans.Selected is not null, Chord: Key.Enter),
        new(
            KeyContext.Plans,
            PlanView.EditKey,
            Revising.Title,
            s => s.EditSelectedPlan(),
            s => s.Plans.Selected is not null,
            Category: ActionCategory.Project),

        // **Proposed roadmaps.**
        new(KeyContext.Roadmaps, default, RoadmapsRegion.ReviewTitle, s => s.ReviewSelected(), s => s.Roadmaps.Selected is not null, Chord: Key.Enter),

        // **A project's settings.** Offered only on a row, so the key is never named where it would change nothing.
        // Enter does the same, reserved above, because Enter on a setting is what somebody presses first.
        new(
            KeyContext.Configuration,
            'c',
            ConfigurationRegion.ChangeTitle,
            s => s.ChangeSetting(),
            s => s.Configuration.Selected is not null),

        // **The box.** Named by its chord, Enter. The box takes Enter itself and marks it handled, so the chord is a
        // name and never a second send.
        new(KeyContext.Line, default, SendTitle, s => s.SendLine(), s => s.Held is not null, Owing.Requesting, Key.Enter),
        new(KeyContext.Line, default, BreakTitle, s => s.BreakLine(), s => s.Held is not null, BreakAlso, Key.Enter.WithShift),

        // **The conversations half of a project view.** Sorting and the archived toggle are view state; archiving is
        // reversible and asks nothing; destroying is behind the full door.
        new(KeyContext.Conversations, 's', SortTitle, s => s.SortConversations(), KeyCommand.Always),
        new(
            KeyContext.Conversations,
            'v',
            ShowArchivedTitle,
            s => s.ToggleArchived(),
            KeyCommand.Always,
            Says: s => s._showArchived ? HideArchivedTitle : ShowArchivedTitle),

        // Starting one belongs here rather than in a transcript of a different one. **Offered whenever this terminal
        // can speak as the person at all.**
        new(KeyContext.Conversations, 'k', Participation.StartTitle, s => s.Start(), s => s.Seats is not null, Owing.Requesting),

        new(
            KeyContext.Conversations,
            'h',
            ArchiveTitle,
            s => s.ArchiveSelected(),
            s => s.Conversations.Selected is not null,
            Says: s => s.Conversations.Selected is { Archived: true } ? UnarchiveTitle : ArchiveTitle),
        new(KeyContext.Conversations, 'l', Files.ConversationTitle, s => s.ShowConversationFiles(), s => s.Conversations.Selected is not null),
        new(KeyContext.Conversations, 'f', Closing.Title, s => s.CloseSelected(), s => s.Conversations.Selected is not null),
        new(KeyContext.Conversations, 'd', Destroying.Title, s => s.DestroySelected(), s => s.Conversations.Selected is not null),

        // The deployment's, as on the projects list and for the same reason.
        new(KeyContext.Conversations, 'y', HealthTitle, s => s.ShowHealth(), KeyCommand.Always, Category: ActionCategory.Deployment),
        new(KeyContext.Conversations, 'b', OpenConsoleTitle, s => s.OpenConsole(), KeyCommand.Always, Category: ActionCategory.Deployment),

        // **A transcript.** Taking part needs a seat, because every one of these speaks as somebody. Without one the
        // pane still shows a transcript; it simply has no voice to use in it.
        new(KeyContext.Transcript, 'a', Participation.ResultTitle, s => s.SendResult(), s => s.Held is not null),
        new(KeyContext.Transcript, 'q', Participation.ClarifyTitle, s => s.Clarify(), s => s.Held is not null),
        new(KeyContext.Transcript, 'k', Participation.RequestTitle, s => s.SendRequest(), s => s.Held is not null, Owing.Requesting),

        // **Copying does not need a seat**: reading a conversation and taking a copy of what you read are the same act,
        // and a person with no seat is the operator most likely to be quoting a transcript. **What is on the screen
        // rather than what is in the deployment**: copying and folding change nothing anybody else can see, which is
        // the line the View category draws.
        new(KeyContext.Transcript, CopyKey, CopyTitle, s => s.Copy(), s => s.Transcript.Open is not null, Category: ActionCategory.View),
        new(
            KeyContext.Transcript,
            CopyMessageKey,
            CopyMessageTitle,
            s => s.CopyMessage(),
            s => s.Transcript.Open is not null,
            Category: ActionCategory.View),
        new(
            KeyContext.Transcript,
            FoldKey,
            FoldTitle,
            s => s.Fold(),
            s => s.Transcript.Open is not null,
            Category: ActionCategory.View,
            Says: s => s.Transcript.Folded ? UnfoldTitle : FoldTitle),

        // **Back to the roadmap from an item's conversation** (SPEC.md §5.7), offered only where the broker's
        // reservation says this is a live item's: a removed item's conversation belongs to no roadmap any more.
        new(KeyContext.Transcript, ItemRoadmapKey, ItemRoadmapTitle, s => s.OpenItemRoadmap(), s => s.Transcript.Item is { Removed: false }),

        // **Recognised by the conversation's identifier and never by anything in a payload.** §13 tells a receiver a
        // payload is inert data, so an ownership discussion is known by the name it was opened under. `n` is what it
        // is in the agents pane, because it is the same operation reached from the other end. Needs no seat: accepting
        // is an operator operation and reading the transcript is how somebody decides to. **The agent's**, though it
        // is reached from a transcript: what it decides is what one agent's peers will read.
        new(
            KeyContext.Transcript,
            'n',
            Ownership.AcceptTitle,
            s => s.AcceptProposed(),
            s => OwnershipAsk.Recognises(s.Transcript.Open),
            Category: ActionCategory.Agent),

        // **The message line**, a layer on every place: it is the surface's own rows and belongs to no pane. Neither
        // touches the deployment: one shows what was said in full and the other gives the rows back. Each is offered
        // only while there is something to act on.
        new(KeyContext.MessageLine, WholeKey, WholeTitle, s => s.Whole(), s => s.SaidMore, Category: ActionCategory.View),
        new(KeyContext.MessageLine, ClearKey, ClearTitle, s => s.Clear(), s => s.Said.Length > 0, Category: ActionCategory.View),

        // **Everywhere**, a layer on every place: what the keys are, which build this is, and what is about the project
        // rather than the pane the cursor is in. Back is on the bar under View, so it is never only a key; Escape is
        // answered by `OnKeyDown` first, and this entry names it and runs the same delegate so the two cannot drift.
        new(
            KeyContext.Everywhere,
            default,
            BackStroke,
            s => s.BackAsync(),
            s => s._place.CanGoBack,
            Chord: Key.Esc,
            Category: ActionCategory.View,
            Order: 1,
            Says: s => s.BackLabel ?? BackStroke),

        // **The way into a project's settings, from wherever a project is in view.** A setting of the project is
        // neither the agents nor the conversations.
        new(
            KeyContext.Everywhere,
            default,
            ConfigurationTitle,
            s => s.OpenConfiguration(),
            s => s._place.Viewing is not Viewing.Configuration and not Viewing.Conversation
                 && (s.Current ?? s.Projects.Selected) is not null,
            Chord: ConfigurationKey,
            Category: ActionCategory.Project,
            Order: 100),

        // **What agents have put forward, from wherever a project is in view** -- a conversation included, which is
        // where a roadmap is most often agreed, so a person never has to leave one to decide it.
        new(
            KeyContext.Everywhere,
            default,
            Proposing.Title,
            s => s.OpenProposals(),
            s => s._place.Viewing is not Viewing.Configuration && (s.Current ?? s.Projects.Selected) is not null,
            Chord: ProposalsKey,
            Category: ActionCategory.Project,
            Order: 90),

        // **What sweeps of the file location found**, beside the proposals for the same reason, and offered whether or
        // not any are waiting so the dismissed ones can be read again.
        new(
            KeyContext.Everywhere,
            default,
            FilesProblems.Title,
            s => s.OpenFileProblems(),
            s => s._place.Viewing is not Viewing.Configuration && (s.Current ?? s.Projects.Selected) is not null,
            Chord: FileProblemsKey,
            Category: ActionCategory.Project,
            Order: 95),

        new(KeyContext.Everywhere, ActionsKey, KeysTitle, s => s.Offer(), KeyCommand.Always, Category: ActionCategory.Help, Order: 1),
        new(KeyContext.Everywhere, default, AboutTitle, s => s.About(), KeyCommand.Always, Chord: Key.F1, Category: ActionCategory.Help, Order: 2),
    ];
}
