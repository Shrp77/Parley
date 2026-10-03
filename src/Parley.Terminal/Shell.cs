using Parley.Setup;
using System.Drawing;
using System.Text;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// The whole surface: three views built from four regions, a status line, and the reads that fill
/// them.
/// </summary>
/// <remarks>
/// <para>
/// <b>One thing at a time, and this paragraph used to say the opposite.</b> It described 005's
/// design — the projects, the agents and a conversation in front of somebody together — which is
/// the arrangement the owner ran and called "confusing and too busy", and which 006 replaced with
/// three drill-down views. <see cref="Panes"/> has carried the correct account since; this summary
/// did not, so the file contradicted itself three hundred lines apart. Found by a review lens
/// asking whether the remarks still describe the code, which is not a question reading the code
/// answers.
/// </para>
/// <para>
/// <b>Side by side above the threshold, stacked below it.</b> The same four view objects either
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

    /// <summary>How many rows the menu bar takes, which is one and always.</summary>
    /// <remarks>
    /// Named rather than written as <c>1</c> at five sites, because the five have to agree: the panes
    /// start below it and their heights are computed from what is left. A literal in four of five places
    /// is how a row comes to be counted twice or not at all.
    /// </remarks>
    internal const int BarRows = 1;

    /// <summary>The most rows the message line may take.</summary>
    /// <remarks>
    /// A paragraph, which is what <see cref="FormDialog.Deepest"/> settles on for the same
    /// question and for the same reason: a screen is one screen, and a message free to grow
    /// without limit would push the regions somebody opened this to read off the top of it.
    /// Whatever does not fit is one keystroke away in <see cref="Saying"/>, which scrolls.
    /// </remarks>
    internal const int MessageDeepest = 8;

    /// <summary>How many rows the regions keep whatever is being said.</summary>
    /// <remarks>
    /// A frame is two rows and a list of nothing is not a list, so six is the least that still
    /// reads as a region. On a short terminal this is what bounds the message rather than
    /// <see cref="MessageDeepest"/>.
    /// </remarks>
    internal const int LeastRegionRows = 6;

    /// <summary>What the line's send is called.</summary>
    internal const string SendTitle = "Send what you wrote";

    /// <summary>What adding a line break is called.</summary>
    internal const string BreakTitle = "Start a new line";

    /// <summary>The other way to add one, for a terminal that cannot tell the chord from a bare Enter.</summary>
    internal const string BreakAlso = "or Ctrl+Enter, or type a backslash and press Enter";

    /// <summary>Adds a line break to what is being written.</summary>
    /// <returns>When it has been added.</returns>
    private Task BreakLine()
    {
        Line.Break();

        return Task.CompletedTask;
    }

    /// <summary>Records who the line in this conversation goes to, so a restart keeps it.</summary>
    /// <remarks>
    /// Called wherever the recipients change. Everyone is recorded as nothing, including a line that was narrowed and
    /// then ticked back to every participant — otherwise a remembered list would quietly leave out an agent that joins
    /// later, which is the opposite of what "everyone" meant when it was chosen.
    /// </remarks>
    private void RememberAddressing()
    {
        if (Drafting is not { } draft || _drafted is not { } at)
        {
            return;
        }

        var everyone = !draft.Narrowed || draft.Pairs.All(pair => draft.Recipients.Contains(pair.Peer));

        Addressing.Remember(at.Project, at.Conversation, everyone ? null : draft.Recipients);
    }

    /// <summary>What including or excluding a participant is called.</summary>
    internal const string IncludeTitle = "Include or exclude this agent";

    /// <summary>Includes or excludes the participant under the cursor.</summary>
    /// <returns>When the column has been redrawn.</returns>
    /// <remarks>
    /// The same act Enter on that row performs, raised through the same event, so the two routes cannot come
    /// to mean different things -- which is what `contracts/views.md` asks for when it says a pointer reaches
    /// whatever a key does.
    /// </remarks>
    private Task ToggleOne()
    {
        Room.Toggle();

        return Task.CompletedTask;
    }

    /// <summary>Sends what is written, as the preview said it would.</summary>
    /// <returns>When the send has been queued.</returns>
    /// <remarks>
    /// <b>The seat is read here rather than when the line was focused.</b> 0.18.9 found three call sites that
    /// captured it when a form opened and went on speaking under a name the book had released; the rule since is
    /// that the send asks. The plan is the draft's own, not recomputed: a second derivation between the preview
    /// and the send would be a second opinion about what was promised.
    /// </remarks>
    private Task SendLine()
    {
        if (_place.Title is not { } conversation || Current is not { } place)
        {
            Said = SeatWithoutProject;

            return Task.CompletedTask;
        }

        if (Drafting is not { } draft)
        {
            // **Not the project, and not the seat.** Who a line goes to is read from the conversation, and until that
            // read lands there is no plan to send by. This was folded into the message above, which is how the owner
            // was told on 0.36.0 that a project they were looking at did not resolve. The read is asked for here, so
            // "in a moment" is true rather than hopeful.
            Said = NotReadYet;
            Queue(token => OpenAsync(place, conversation, token));

            return Task.CompletedTask;
        }

        // **A command still in the box at Enter is one that nothing consumed.** Completing an agent takes the
        // command and its parameter out of the text, so the ordinary path never reaches here with one; what does
        // is somebody who typed it out by hand, and they are owed either the act or the reason (FR-023).
        if (Commands.Resolve(Line.Written) is { Command: { } command } written)
        {
            Said = Ran(command, written.Rest);

            return Task.CompletedTask;
        }

        // An empty line is not checked here. The send answers it without reaching a broker, and one sentence
        // about it in one place cannot come to disagree with itself.
        //
        // **What is sent is what is left once the addressing is taken out**, by the record of what a
        // completion wrote rather than by reading the words: nothing routes into a payload §13 tells the
        // receiver to treat as inert data.
        draft.Text = draft.Says(Line.Written);

        Queue(async token =>
        {
            // **Enter is a key, so a person who was away is back** (specs/023). The idle sweep dropped only the stream;
            // the seat is the broker's and was never released, so refusing with "not connected" would make somebody
            // press Enter twice to send once. Taken back here, in the queue, rather than trusted to `Touched` having
            // finished first.
            if (Held is null)
            {
                await EnsureSeatAsync(place, null, token).ConfigureAwait(false);
            }

            // **And the draft may be from before they went.** While away the follow reads the conversation and leaves
            // the draft alone, because there is no stream to derive "owed by you" from — so a Request that arrived
            // meanwhile was answered with a new Request, the agent's question left open and the person shown as owing it
            // (the review gate's finding). A draft derived through a stream that is not the one held now is read again,
            // which rebuilds it from what the conversation is, keeping the narrowing and the chips.
            if (Held is not null && !ReferenceEquals(_draftedWith, Held))
            {
                await OpenAsync(place, conversation, token).ConfigureAwait(false);
            }

            if (!ReferenceEquals(Drafting, draft))
            {
                if (Drafting is not { } fresh)
                {
                    Said = NotReadYet;

                    return;
                }

                // Somebody joined or left since the preview was drawn, so the line would not go where it said. Shown,
                // not sent: who a line reaches is the one thing the preview promises.
                if (!fresh.Recipients.ToHashSet().SetEquals(draft.Recipients))
                {
                    Said = RecipientsChangedWhileAway;

                    return;
                }

                fresh.Text = fresh.Says(Line.Written);
                draft = fresh;
            }

            if (Held is not { } hold || Voice() is not { } voice)
            {
                Said = NotConnected;

                return;
            }

            var sent = await voice.SendAsync(hold, draft, conversation, token).ConfigureAwait(false);

            Said = sent.Says;

            // **Cleared only on a send that reached somebody**, and decided by the count rather than by the
            // words. A first version matched the report's opening -- which is not the same question, and it
            // cleared an over-long draft the cap had refused. 0.25.1 records what that costs: the cap was
            // enforced after the form closed and took the message with it.
            if (sent.Count == 0)
            {
                return;
            }

            Line.Written = string.Empty;

            // The view's project, not the list's selection: the two are different accessors for one question, which is
            // this repository's most-repeated defect.
            await OpenAsync(place, conversation, token).ConfigureAwait(false);
        });

        return Task.CompletedTask;
    }

    /// <summary>Runs a command typed out by hand, and says what came of it.</summary>
    /// <param name="command">The command the identifier resolved to.</param>
    /// <param name="rest">What followed it.</param>
    /// <returns>The sentence to put in front of the person.</returns>
    /// <remarks>
    /// <para>
    /// <b>A <c>Draft</c> command is not sendable alone</b> (FR-024), and the reason is the protocol's rather than
    /// this surface's: membership is derived (SPEC.md §5.0) and an agent joins by being party to an exchange, so
    /// <c>/add</c> with nothing to say would have to invent a Request — and every message is answered.
    /// </para>
    /// <para>
    /// The box is never cleared on this path. The command is still what somebody wrote, and a refusal that took
    /// it away would be 0.25.1 again.
    /// </para>
    /// <para>
    /// The parameter type is qualified because <c>Terminal.Gui.Input.Command</c> is a different thing of the same
    /// name — the toolkit's bindable action. The same collision as 017's <c>Owing</c>, and the same answer: say
    /// which one.
    /// </para>
    /// </remarks>
    private string Ran(Parley.Client.Seating.Command command, string rest)
    {
        var given = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (Commands.Wrong(command, given.Length) is { } wrong)
        {
            return wrong;
        }

        if (command.Kind == Does.Draft && Drafting is { } draft && command.Id == Adds)
        {
            if (!AgentName.TryCreate(given[0], out var peer))
            {
                return $"{Safe.Text(given[0])} is not an agent name, so nothing was added.";
            }

            draft.Widen(peer);
            RememberAddressing();
            Line.Written = string.Empty;
            Line.Show(draft, seated: true);

            return $"{peer.Value} will get this as well. Type what you want to say.";
        }

        return $"/{command.Id} changes what is about to be sent and sends nothing of its own. "
             + "Write something to go with it.";
    }

    /// <summary>The one command this surface reads by name, because widening a draft is not a send.</summary>
    /// <remarks>
    /// Named once rather than spelled at each of its three uses. Every other command runs through the table, and
    /// this one has to be recognised before it is run: what it offers to complete, and what its completion does,
    /// are both decided before there is anything to send.
    /// </remarks>
    private const string Adds = "add";

    /// <summary>What the box is completing now, derived from what is written.</summary>
    private Completion _listing = Completion.Read(string.Empty, [], []);

    /// <summary>Redraws the preview and the completion list for what is written now.</summary>
    /// <remarks>
    /// <para>
    /// The text is read from the box rather than tracked beside it, so there is one copy of what somebody typed.
    /// Two would eventually disagree and the wrong one would be what got sent.
    /// </para>
    /// <para>
    /// <b>The list is recomputed here and never remembered</b>, which is the whole of FR-034: deleting back into
    /// a match brings it back because nothing was dismissed. A flag saying somebody had closed it would be
    /// 0.29.1's cached waiting count in a new place.
    /// </para>
    /// </remarks>
    private void Retyped()
    {
        _listing = Completion.Read(Line.Written, Commands.Ids, Completable(), _listing.Selected);
        Line.Offer(_listing);

        if (Drafting is not { } draft)
        {
            Line.Show(null, Held is not null);

            return;
        }

        draft.Text = draft.Says(Line.Written);
        Line.Show(draft, seated: true, Near());
    }

    /// <summary>
    /// What <c>@</c> offers, which depends on what the line is already doing.
    /// </summary>
    /// <returns>The names, in the order they are offered.</returns>
    /// <remarks>
    /// <b>Two candidate sets, because the two questions are different.</b> A bare <c>@</c> narrows the message to
    /// somebody already in the conversation, so it offers the participants and <c>all</c>. <c>/add @</c> widens it
    /// to somebody who is not, so it offers the rest of the project's roster — and offering a participant there
    /// would be an add that adds nobody.
    /// </remarks>
    private IReadOnlyList<string> Completable()
    {
        var participants = Drafting is { } draft
            ? draft.Pairs.Select(p => p.Peer.Value).ToList()
            : [];

        if (Commands.Resolve(Line.Written).Command is { Id: Adds })
        {
            return [.. Peers().Except(participants, StringComparer.Ordinal)];
        }

        return [Draft.All, .. participants];
    }

    /// <summary>The near miss to put above the plan, or null.</summary>
    /// <returns>The note.</returns>
    /// <remarks>
    /// FR-033. An identifier that did not resolve is text and is sent as written (FR-021), so the only defence
    /// against a typo is that it is visible before Enter rather than in somebody's inbox afterwards.
    /// </remarks>
    private string? Near() =>
        Commands.Resolve(Line.Written).NearMiss is { } nearly
            ? $"this is text, not a command — did you mean /{nearly}?"
            : null;

    /// <summary>Puts the completion list's selection into the box, and resolves it if it was an agent.</summary>
    /// <remarks>
    /// <para>
    /// <b>A chosen agent leaves the text and becomes a recipient.</b> That is what the specification means by a
    /// chip: nothing is parsed out of prose when Enter is pressed, so a semicolon in an ask is just a semicolon
    /// and §13's envelope never carries a routing directive it exists to have ignored. What shows somebody their
    /// chips is the preview's own <c>to:</c> line.
    /// </para>
    /// <para>
    /// <c>/add</c> goes with its parameter, because the command's whole purpose was to carry it. What is left in
    /// the box after a completion is always prose.
    /// </para>
    /// </remarks>
    private void Complete()
    {
        var chosen = _listing.Completes();
        var filled = _listing.Fills(Line.Written);

        if (_listing.Which == Sigil.Agent && chosen is not null && Drafting is { } draft)
        {
            var adding = Commands.Resolve(Line.Written).Command is { Id: Adds };

            if (string.Equals(chosen, Draft.All, StringComparison.Ordinal))
            {
                draft.AddressEveryone();
            }
            else if (AgentName.TryCreate(chosen, out var peer))
            {
                if (adding)
                {
                    draft.Widen(peer);
                }
                else
                {
                    draft.Narrow(peer);
                }
            }

            RememberAddressing();

            // **The name stays in the box, and only the command that carried it goes.** `/add` has done its
            // work the moment the agent is resolved, so leaving it there would be a command nothing consumed
            // at the next Enter; the chip beside it is what says who was added.
            var chip = "@" + chosen;

            filled = adding ? chip + " " : filled;

            draft.Chip(chip);
        }

        Line.Written = filled;
        Retyped();
    }


    /// <summary>What showing headers only is called.</summary>
    internal const string FoldTitle = "Fold to one line per message";

    /// <summary>What showing everything again is called.</summary>
    /// <remarks>
    /// Two labels rather than one that says "toggle": a status line is read at a glance, and a person wants
    /// to know what the key will do rather than that it has two states.
    /// </remarks>
    internal const string UnfoldTitle = "Show the messages again";

    /// <summary>The key that folds and unfolds the transcript.</summary>
    /// <remarks>
    /// <c>f</c> is taken by nothing on this pane. Not a chord, because this pane is a list: every letter is
    /// free here, which is exactly what the input line beside it cannot say.
    /// </remarks>
    internal const char FoldKey = 'f';

    /// <summary>Shows headers only, or everything again.</summary>
    /// <returns>When the transcript has been redrawn.</returns>
    /// <remarks>
    /// The status line changes with it, because its own label is what says which way the key now goes.
    /// </remarks>
    private Task Fold()
    {
        Transcript.Fold();
        Relay();

        return Task.CompletedTask;
    }

    /// <summary>What copying the open conversation is called.</summary>
    internal const string CopyTitle = "Copy this conversation to the clipboard";

    /// <summary>The key that copies it.</summary>
    internal const char CopyKey = 'c';

    /// <summary>What copying the one message under the cursor is called.</summary>
    internal const string CopyMessageTitle = "Copy this message to the clipboard";

    /// <summary>The key that copies one message, the owner's choice of 2026-10-02.</summary>
    internal const char CopyMessageKey = 'm';

    /// <summary>What is said when the cursor is not on a message, so nothing was copied.</summary>
    internal const string NoMessageHere =
        "The cursor is not on a message, so nothing was copied. Move it onto one, or Alt+c copies the whole conversation.";

    /// <summary>What is said when one message went.</summary>
    /// <param name="characters">How long it was.</param>
    /// <returns>The sentence.</returns>
    internal static string CopiedMessage(int characters) =>
        $"Copied the message to the clipboard ({characters} characters).";

    /// <summary>What is said when the copy went.</summary>
    /// <remarks>
    /// The size is in it because a clipboard gives no other evidence that anything happened: a
    /// person presses a key, the screen does not change, and the only way to find out whether it
    /// worked used to be to paste somewhere and look.
    /// </remarks>
    internal static string Copied(int characters) =>
        $"Copied the conversation to the clipboard ({characters} characters).";

    /// <summary>What is said when it did not.</summary>
    /// <remarks>
    /// Named rather than silent, and it names the way round it: a clipboard needs a tool this
    /// machine may not have (<c>xclip</c> on Linux), and over SSH it is the <em>server's</em>
    /// clipboard the toolkit can reach in any case, which is rarely the one somebody wants.
    /// </remarks>
    internal const string NotCopied =
        "This machine's clipboard could not be written to. F8 opens this conversation in the web UI, where a "
        + "browser can copy or save it.";

    /// <summary>What clearing the message line is called.</summary>
    internal const string ClearTitle = "Clear the message";

    /// <summary>What reading a message too long for the line is called.</summary>
    internal const string WholeTitle = "Show the whole message";

    /// <summary>The key that clears the message line.</summary>
    internal const char ClearKey = 'x';

    /// <summary>The key that shows a message the line could not hold.</summary>
    /// <remarks>
    /// <c>e</c>, for expand, since 2026-10-02: it was <c>m</c>, and the owner asked for Alt+M to copy one message. The
    /// copy is used far more often than this, which appears only when the message line has been cut off.
    /// </remarks>
    internal const char WholeKey = 'e';

    private readonly BrokerDirectory _directory;
    private readonly Plain _status = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };

    /// <summary>The menu bar, on the top row, always visible.</summary>
    /// <remarks>
    /// <b>021's D3, and it costs a row permanently.</b> The owner chose that over a popover summoned with
    /// Alt, which costs none, on discoverability: the menus can be seen to exist without knowing to press
    /// anything, which is the whole complaint -- every action was behind <c>?</c> and <c>?</c> was behind
    /// knowing about <c>?</c>. The row joins <see cref="HeightsLocked"/>'s arithmetic rather than being
    /// subtracted somewhere new, because this surface has been bitten three times by a row that came and
    /// went and resized every region under it.
    /// </remarks>
    private readonly MenuBarView _bar;

    /// <summary>The title of the menu dropped down from the bar now, or <see langword="null"/>.</summary>
    internal string? MenuOpen => _bar.Opened;

    /// <summary>
    /// Where every outcome this surface reports is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It did not exist, and <see cref="Said"/> was written to nothing.</b> Twenty-odd
    /// places set it — seated as somebody, the broker refused the join, sorted by name, showing
    /// archived, an agent retired, a seat released, a log that could not be read — and fifteen test
    /// files asserted on it, and no line of this surface ever drew it. Every one of those messages
    /// was computed, checked, and shown to nobody. The owner met it as `j`, a name typed, and
    /// nothing happening.
    /// </para>
    /// <para>
    /// That is the same shape as the terminal having no colour while <c>ColourTests</c> passed: a
    /// property the suite holds to and the screen never shows. A test that reads
    /// <see cref="Said"/> is testing a string; what makes it a message is this label.
    /// </para>
    /// <para>
    /// <b>Always present rather than appearing when there is something to say.</b> A row that comes
    /// and goes resizes every region under it, and this surface has been bitten three times by what
    /// happens when regions are resized or rearranged while a read is landing — twice in the review
    /// gate for 006 alone. One reserved row costs less than a race, and a fixed place to look costs
    /// a person nothing at all.
    /// </para>
    /// </remarks>
    private readonly Plain _message = new() { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill() };

    /// <summary>How many rows the message line is occupying.</summary>
    /// <remarks>
    /// <b>One row was not enough and the owner met it.</b> A broadcast to nineteen agents reports a
    /// count, a cause and the names — and the cause is a broker's own words, so the line ran off
    /// the end of the screen mid-sentence, in the one place this surface says what happened. The
    /// row is as many rows as the message needs now, up to <see cref="MessageDeepest"/>, and the
    /// panes above it are sized from this rather than from a constant.
    /// </remarks>
    private int _messageRows = 1;

    /// <summary>What is being said, before it was wrapped to the screen.</summary>
    private string _said = string.Empty;

    private readonly List<View> _regions;
    private readonly CancellationTokenSource _reading = new();

    // Following the open transcript: the current follow, whether it has a read waiting in the queue,
    // and the warning it last put on the message line -- kept so that recovering clears its own words
    // and never somebody else's.
    private readonly Lock _followGate = new();
    private CancellationTokenSource? _following;
    private int _followQueued;
    private string? _followFailed;
    private CancellationTokenSource? _watchingMail;
    private int _mailQueued;

    private bool _stacked;

    /// <summary>Whether the regions have been given a size at least once.</summary>
    private bool _arranged;
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

    // The application's keyboard, once hooked, so every key counts as being here (`AnyKey`) and the hook is let go on
    // dispose.
    private IKeyboard? _keyboard;

    internal Shell(BrokerDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _directory = directory;

        Title = $"parley {BuildIdentity.Version}";
        BorderStyle = LineStyle.None;

        Projects = new ProjectsRegion();
        Agents = new AgentsRegion();
        Conversations = new ConversationsRegion();
        Transcript = new TranscriptRegion();
        Room = new RoomRegion();

        // **Enter on something owed reads it where it was said.** The primary action of the active control
        // (FR-031): in this list, activating a row. A row that points at no message does nothing rather than
        // moving the reader somewhere arbitrary, and says so.
        // **The checklist is the draft's recipients, drawn.** `@` and this column are two ways at one thing,
        // so neither may keep its own idea of who a message is going to -- the preview above the box and the
        // ticks beside it would be two answers to one question, and only one of them would be sent.
        Room.Toggling += (_, peer) =>
        {
            if (Held is null)
            {
                Said = RoomRegion.NoSeat;

                return;
            }

            if (Drafting is not { } draft)
            {
                return;
            }

            if (!draft.Recipients.Contains(peer))
            {
                draft.Narrow(peer);
            }
            else if (draft.Recipients.Count == 1)
            {
                // **Refused rather than allowed to empty**, because a message addressed to nobody is not a
                // state worth being in: the box would go on saying there is nobody in this conversation while
                // somebody typed into it.
                Said = RoomRegion.LastOne;

                return;
            }
            else
            {
                draft.AddressTo([.. draft.Recipients.Where(addressed => addressed != peer)]);
            }

            Room.Address(draft.Recipients);
            RememberAddressing();

            // Guarded for the reason the seat's own setter is: this is wired before the line exists, and the
            // compiler cannot see that nothing raises it until somebody presses a key.
            Line?.Show(draft, seated: true, Near());
        };

        // **Enter on an agent is somebody about to write**, so it puts them in the box with the ticks as they were (the
        // owner's call of 2026-10-02). Space is the tick.
        Room.Choosing += (_, _) =>
        {
            if (Line is not null)
            {
                Focus(Panes.ToList().IndexOf(Line));
            }
        };

        Room.Reading += (_, item) =>
        {
            if (item.At is not { } at)
            {
                Said = RoomRegion.Unreachable;

                return;
            }

            if (Transcript.Reach(at))
            {
                Focus(Panes.ToList().IndexOf(Transcript));
            }
            else
            {
                Said = RoomRegion.Unreachable;
            }
        };
        Line = new LineRegion();
        Configuration = new ConfigurationRegion();

        // Every region, because this is the list the arrangement hides from. The room and the line were left off it
        // when 017 added them, so after a conversation had given them a size, going back left both drawn over the
        // project view. `LeavingTheRoomTests` asks the regions themselves rather than this list.
        _regions = [Projects, Agents, Conversations, Transcript, Room, Line, Configuration];

        // **The record of which pane has focus follows focus, however it moved** (the owner's report of 2026-10-03).
        // `FocusedRegion` changed only through `Focus`, so a pane reached by the mouse had focus on screen and not in the
        // record — and every re-arrangement re-applies the record. The mail poll re-arranges every five seconds, so
        // somebody who clicked into the box was moved to the transcript mid-word. The shell's own `Focus` raises this
        // too and finds the record already right.
        foreach (var region in new View[] { Projects, Agents, Conversations, Transcript, Room, Line, Configuration })
        {
            region.HasFocusChanged += (sender, _) => Followed(sender as View);
        }

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

            // **Going there reads it, so this does not read it first**: two reads on one keystroke is one
            // wasted export and two chances for them to disagree.
            //
            // **Which means the place has to name the conversation, and it did not from two of the three
            // views.** This passed `_place` unchanged unless it was a project view, and read the conversation
            // itself — so from the projects view nothing named it, and from a conversation the place went on
            // naming the *previous* one while the transcript showed the new one. Tolerable while the place was
            // not what read; a defect the moment it is.
            await GoAsync(_place.Viewing switch
            {
                Viewing.Project => _place.Into(conversation.Id),
                Viewing.Conversation => _place.Back().Into(conversation.Id),
                _ => ViewPlace.Into(project.Name).Into(conversation.Id),
            }).ConfigureAwait(false);
        });

        // A bar rather than a line in the terminal's own colours, and the one place this surface
        // paints a background of its own. It is the only thing on screen that is always there, and
        // what it says -- where you are, how to leave, what this region can do -- is what somebody
        // who is lost reads. A conventional bar is found without being looked for.
        _status.SetScheme(Colours.StatusBar());

        // No scheme of its own, deliberately. The bar paints a background because it is
        // furniture that must be found without being looked for; a message is one sentence in
        // the terminal's own colours, and Colours' rule is that every colour is derived from a
        // word already on the line -- which a message line, being anything at all, has not got.

        // One row until something is said that needs more, and the height is given outright rather
        // than left to the label: `Show` wraps the text itself, so the rows are counted rather than
        // guessed at, and the panes above are sized from the same number.
        _message.Height = 1;

        // Every region joins the tree here and none ever leaves it. ArrangeLocked carries why:
        // reparenting on a change of view races the main loop's own walk of this list.
        // Painted like the status line, because it is the same kind of thing: furniture that has to be
        // found without being looked for. The two together bracket the screen.
        _bar = new MenuBarView(() => MenuRow.Menus(Bar()), RunFromMenu) { X = 0, Y = 0, Width = Dim.Fill(), Height = BarRows };
        _bar.SetScheme(Colours.StatusBar());

        Add(_bar);
        Add(_message);
        Add(_status);
        Add(Projects);
        Add(Agents);
        Add(Conversations);
        Add(Transcript);
        Add(Room);
        Add(Line);
        Add(Configuration);

        // Enter in the box sends, and the preview follows what is written. Subscribed here rather than in the
        // region, which knows nothing about seats or brokers.
        Line.Show(null, seated: false);
        Line.Sending += (_, _) => _ = SendLine();

        // Heights alone, for the message line's reason: this fires on a keystroke, and a rearrangement ends by
        // taking focus away from the box somebody is typing in.
        Line.Resized += (_, _) =>
        {
            lock (_screen)
            {
                HeightsLocked();
            }
        };

        Line.Typing += (_, _) => Retyped();

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

    /// <summary>The conversation's narrow column: who is here, and what is owed.</summary>
    internal RoomRegion Room { get; }

    /// <summary>The one line a person speaks from.</summary>
    internal LineRegion Line { get; }

    /// <summary>One project's settings, each with what it means.</summary>
    internal ConfigurationRegion Configuration { get; }

    /// <summary>What is written and not yet sent, or null when there is no seat to send it under.</summary>
    /// <remarks>
    /// Rebuilt whenever the room is filled, because the acts it derives depend on who owes whom and that changes
    /// under a reader: a peer answering while somebody types moves a Clarify to a Request. Held rather than
    /// derived per keystroke only because the answer-or-ask-back choice is the person's and has to persist.
    /// </remarks>
    internal Draft? Drafting { get; private set; }

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
        // **Two columns since 0.30.0, and the transcript is the one that did not change.** The room was
        // built around it rather than in place of it: forty-nine test files in this suite drive the
        // conversation view, and almost all of them are about the transcript -- the column-zero rule that
        // stops a payload forging a header, agent-written text escaped on the way to the screen, wrapping,
        // reflow, copying, the refill race. Every one of those was found the hard way and several by probe, so
        // they keep passing by construction rather than by care.
        Viewing.Conversation => [Room, Transcript, Line],
        Viewing.Configuration => [Configuration],
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
    /// <para>
    /// The four views are shown and hidden rather than rebuilt, so a selection, a scroll position
    /// and a test's reference to a region all survive a resize. Rebuilding them would lose the
    /// person's place every time somebody dragged a window edge.
    /// </para>
    /// <para>
    /// <b>"Has this been arranged yet" is asked outright, and it used to be guessed.</b> The
    /// condition was <c>SubViews.Count &gt; 1</c>, which was true only once a pane had been added
    /// — a fair proxy while <c>ArrangeLocked</c> reparented, and silently wrong the moment it
    /// stopped: every region joins the tree in the constructor now, so the count is above one
    /// before anything has been laid out, and the early return fired on the very first call. The
    /// regions kept their default size, which is none, and the surface drew an empty screen. Two
    /// tests caught it and the rest did not, because opening any view calls <c>Relay</c>, which
    /// does not take this path.
    /// </para>
    /// </remarks>
    internal void Lay(int width)
    {
        var stacked = width < Threshold;

        if (stacked == _stacked && _arranged)
        {
            // The panes stay; the status line does not. Which rung of it fits is a question about the width, and a resize
            // that crossed no threshold used to leave the line chosen for whatever width it was first drawn at — found
            // putting the build on the widest rung, which never appeared on a terminal widened after it opened.
            lock (_screen)
            {
                DescribeLocked();
            }

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

    /// <summary>The action the waiting count points at, so the line and the pane cannot name different keys.</summary>
    /// <returns>Taking delivery.</returns>
    private RegionAction DeliveryAction() =>
        new('w', Participation.DeliveryTitle, TakeDelivery);

    /// <summary>Puts the current view's panes on screen and takes the others off.</summary>
    private void Arrange()
    {
        lock (_screen)
        {
            ArrangeLocked();
        }
    }

    /// <summary>
    /// Arranges, with the screen already held.
    /// </summary>
    /// <remarks>
    /// <b>The regions are added once and never reparented, and that is a fix rather than a
    /// tidy-up.</b> This used to remove all four and add back the current view's, which mutates
    /// the subview list — while the main loop walks that same list in <c>View.CollectAll</c>
    /// during layout. <see cref="_screen"/> locks this method against itself and the toolkit
    /// takes no lock at all, so it never protected against the reader. Eight threads changing
    /// view against three thousand draws threw <c>Collection was modified</c> out of
    /// <c>LayoutSubViews</c> seven hundred and forty-nine times.
    /// <para>
    /// It is reachable by the commonest action the surface has: opening a conversation queues a
    /// read, and that read calls <c>GoAsync</c> after awaiting the broker — on a pool thread.
    /// </para>
    /// <para>
    /// Visibility does the whole job instead. The tree is fixed from the constructor, so there is
    /// nothing for a draw to trip over, and the property this method actually needs — only the
    /// current view's panes on screen — is exactly what <c>Visible</c> says.
    /// </para>
    /// </remarks>
    private void ArrangeLocked()
    {
        var stacked = _stacked;

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
            only.Y = BarRows;
            only.Width = Dim.Fill();
            only.Height = Dim.Fill(1 + _messageRows);
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
                pane.Y = BarRows;
                pane.Width = Dim.Fill();
                pane.Height = Dim.Fill(1 + _messageRows);
            }
        }
        else
        {
            // Two panes, side by side. Agents on the left because the question a project view
            // answers first is who is in it; conversations wider because they carry identifiers and
            // timestamps rather than short names.
            //
            // **The room's own column is narrower than that**, and by a fixed width rather than a share:
            // it holds names and short clauses whatever the terminal is, while the transcript holds
            // whatever a peer wrote and wants every column it can have. A share would widen the one that
            // does not need it in step with the one that does.
            panes[0].X = 0;
            panes[0].Y = BarRows;
            panes[0].Width = _place.Viewing == Viewing.Conversation
                ? Dim.Absolute(Narrow)
                : Dim.Percent(40);

            panes[1].X = Pos.Right(panes[0]);
            panes[1].Y = BarRows;
            panes[1].Width = Dim.Fill();

            // **The third pane is the line, under the transcript rather than beside it.** A conversation is a
            // room: the narrow column on the left, and on the right the transcript with one line to speak from
            // across the bottom of it. The line takes the rows it needs and the transcript takes the rest,
            // which is the arrangement the owner described -- Claude Code, in a layout.
            if (panes.Count > 2)
            {
                panes[2].X = Pos.Right(panes[0]);
                panes[2].Y = Pos.Bottom(panes[1]);
                panes[2].Width = Dim.Fill();
            }
        }

        HeightsLocked();

        _arranged = true;

        Focus(FocusedRegion);
    }

    /// <summary>
    /// Gives each pane on screen its height, with the screen already held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One place, because two callers need it and one of them must not arrange.</b> The message line changes
    /// heights when it grows, and a full rearrangement ends by taking focus — which a message arriving while
    /// somebody types in a dialog must never do. So it asks for heights alone, and this is what it asks.
    /// </para>
    /// <para>
    /// It was a loop over the panes assigning one expression, which is why it had to become a method: the room's
    /// line pane is not <c>Fill</c>, so the loop would have flattened the arrangement the first time a message
    /// grew to two rows — a layout that came apart on the second thing that happened rather than the first.
    /// </para>
    /// </remarks>
    private void HeightsLocked()
    {
        var panes = Panes;
        var below = 1 + _messageRows;

        // The bar is above rather than below, so it is taken off the height and not added to `below`.
        var usable = (Viewport.Height > 0 ? Viewport.Height : 24) - BarRows;

        if (panes.Count > 2 && !_stacked)
        {
            // **What the box asks for, until the transcript is down to its floor.** It was a constant twelve,
            // which is a ceiling on the message rather than a floor under the conversation -- and the box is
            // where an eight-thousand-character request is written.
            var rows = Math.Clamp(
                Line.Rows,
                LineRegion.LeastRows,
                Math.Max(LineRegion.LeastRows, usable - below - LineRegion.TranscriptFloor));

            panes[0].Height = Dim.Fill(below);
            panes[1].Height = Dim.Fill(below + rows);
            panes[2].Height = Dim.Absolute(rows);

            return;
        }

        foreach (var pane in panes)
        {
            pane.Height = Dim.Fill(below);
        }
    }

    /// <summary>How wide the room's own column is, in columns.</summary>
    /// <remarks>
    /// Wide enough for an agent name and the clause beside it -- "billing@thismachine -- waiting on you" is
    /// what this column exists to say -- and no wider, because everything it does not take belongs to the
    /// transcript. Below the stacking threshold the panes stack and this does not apply.
    /// </remarks>
    internal const int Narrow = 34;

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

        DescribeLocked();
    }

    /// <summary>Records a pane that has just gained focus by any route, without moving focus anywhere.</summary>
    /// <param name="region">The pane whose focus changed.</param>
    /// <remarks>
    /// Only a pane of the current view that now has focus is recorded, and only when the record is wrong, so the shell's
    /// own <see cref="Focus"/> — which raises this from inside its lock — changes nothing a second time.
    /// </remarks>
    private void Followed(View? region)
    {
        if (region is not { HasFocus: true })
        {
            return;
        }

        var at = Panes.ToList().IndexOf(region);

        if (at < 0 || at == FocusedRegion)
        {
            return;
        }

        lock (_screen)
        {
            FocusedRegion = at;
            DescribeLocked();
        }
    }

    /// <summary>Writes the status line for wherever the surface is now.</summary>
    /// <remarks>
    /// <b>Split out of <see cref="FocusLocked"/> so that redrawing the line does not move focus.</b>
    /// The captions on it change with the message — clearing is offered only while there is
    /// something to clear — and the only way to rebuild it used to be to take focus again. A
    /// message arriving while a dialog is open would then have called <c>SetFocus</c> on a region
    /// behind that dialog, which is a keystroke landing somewhere nobody is looking.
    /// </remarks>
    private void DescribeLocked()
    {
        var panes = Panes;
        var region = panes[FocusedRegion];

        _bar.Show(BarTitles());

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
        // **Made inert, because neither of these names is this program's.** A project name comes
        // from a manifest somebody may not have written, and a conversation identifier is chosen by
        // an agent (SPEC.md 5.0) -- so an escape sequence in one reaches the one line that is on
        // every screen. A probe put `alpha<ESC>[2Jwiped` in an identifier and the status line
        // carried it through: that clears the operator's terminal, from a peer, by naming a
        // conversation.
        var placed = _place.Viewing == Viewing.Projects
            ? focus
            : $"{Safe.Text(_place.Title)} / {focus}";

        // **The ways out are written next to where you are.** Escape has gone back since 006 was
        // built and nothing on screen ever said so: the owner walked into a project and reported
        // there was no way back to the list. Ctrl+Q was worse -- it was named in a comment and
        // bound nowhere. Both are here, and the menu holds both, so neither is ever only a key.
        var back = BackLabel is { } label ? "  " + label : string.Empty;
        var quit = "  " + QuitLabel;

        // **The message's own two actions, beside the ways out rather than among a region's.**
        // Clearing what was last said is not something the conversations list does; it is
        // something the surface does, and it is offered only while there is something to clear so
        // that the line never advertises an action that would do nothing.
        var about = string.Concat(Messaging().Select(action => "  " + Caption(action)));

        var listed = actions.Count == 0
            ? string.Empty
            : "  " + string.Join("  ", actions.Select(Caption));

        // **`?: actions` is gone from every rung, on the owner's call of 2026-09-28.** It was the line's
        // fallback when a region's actions did not fit: one key naming a dialog that listed the rest. The
        // menu bar is that list now, always on screen and reached with the mouse or Alt, so the fallback
        // named a second way to something already in front of the person. The key itself still works --
        // it is Help > Keys -- it is simply not advertised twice.
        var room = Viewport.Width > 0 ? Viewport.Width : Threshold;

        // **Listed in full when they fit, and left to the menu bar when they do not**, and then further
        // down a ladder that ends holding the thing that matters most: which pane has focus. A status
        // line clipped by the toolkit at whatever column the terminal ends at is the silent truncation
        // the narrow layout exists to avoid, arrived at from the other direction.
        //
        // **The ladder replaced a single either/or, which the way-back label had just broken.** At
        // forty columns `alpha / Focus: Agents  Esc: back to alpha` is over the width before an
        // action is named, and nothing caught it: the guard for this drives the projects view,
        // which has no back label, so it only failed once Ctrl+Q pushed the *top* view one column
        // over as well.
        //
        // Dropping back and quit late is deliberate rather than reluctant. The menu bar holds both, so
        // the short form is not a screen with no way out -- it is the same way out, one menu away.
        // The message's actions sit between the ways out and the region's own, and drop off the
        // ladder before either -- the menu holds them, and unlike Escape they are about something
        // that is already on the screen in front of the person.
        //
        // **The build, on the widest rung and nowhere below it.** Asked for by the owner, who could not tell from the
        // screen which terminal was running while a fix was being installed over it. Last on the line and first to go,
        // because on a narrow terminal the focus and `?` matter more; `?` lists it for that case, and the window title
        // carries it whatever the width.
        var build = "  " + VersionLabel;

        // **What is waiting, and it outranks everything on this line but where you are.** A person looking
        // straight at this surface was being told nothing while work sat in their inbox, and the remedy was a
        // key inside a transcript they had no reason to open. So it survives every rung the focus survives:
        // it is the one thing here that is about them rather than about the screen.
        var mail = Mail is { Certain: true, Waiting: > 0 } held
            ? $"  {held.Waiting} waiting ({Caption(DeliveryAction())})"
            : string.Empty;

        // Beside every rung that still names the ways out, not only the widest: on the projects view the actions alone are
        // wider than two hundred columns, so a build said only beside all of them was said on no terminal anybody has.
        _status.Text = Fits(
            room,
            placed + mail + back + quit + about + listed + build,
            placed + mail + back + quit + about + listed,
            placed + mail + back + quit + about + build,
            placed + mail + back + quit + about,
            placed + mail + back + quit + build,
            placed + mail + back + quit,
            placed + mail,
            focus + mail,
            focus);
    }

    /// <summary>What can be done about the message line right now.</summary>
    /// <returns>The actions, which is none when nothing has been said.</returns>
    /// <remarks>
    /// <b>Global rather than a region's.</b> <see cref="Actions"/> answers for the pane that has
    /// focus, and clearing what the surface last said belongs to no pane — it was reported by the
    /// surface and it is occupying the surface's rows. Offered only while there is something to
    /// act on, so the status line never names a key that would do nothing.
    /// </remarks>
    internal IReadOnlyList<RegionAction> Messaging() =>
    [
        // **The view's, because the message line is the surface's own rows.** Neither of these
        // touches the deployment: one shows what was said in full and the other gives the rows back.
        .. SaidMore
            ? new[] { new RegionAction(WholeKey, WholeTitle, Whole, Category: ActionCategory.View) }
            : [],
        .. Said.Length > 0
            ? new[] { new RegionAction(ClearKey, ClearTitle, Clear, Category: ActionCategory.View) }
            : [],
    ];

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
        _place.CanGoBack ? $"{BackStroke}: back to {Safe.Text(_place.Back().Title)}" : null;

    /// <summary>How one action is written wherever it is offered.</summary>
    /// <param name="action">The action.</param>
    /// <returns>Its key and its label.</returns>
    /// <remarks>
    /// One function so the status line and the menu cannot write it two ways — the menu matches
    /// the entry the person chose back against the action by this string, so two spellings would
    /// be a menu whose entries silently do nothing.
    /// </remarks>
    private static string Caption(RegionAction action) => $"{action.Stroke}: {action.Label}";

    /// <summary>How the menu names an action, which is with what it costs.</summary>
    /// <param name="action">The action.</param>
    /// <returns>The entry.</returns>
    /// <remarks>
    /// The menu has the width for it and the status line does not — see <see cref="RegionAction"/>.
    /// An em dash rather than brackets, because these read as sentences: "Ask a question — opens an
    /// exchange they must answer".
    /// </remarks>
    private static string Detailed(RegionAction action) =>
        action.Costs is { Length: > 0 } costs
            ? $"{Caption(action)} — {costs}"
            : Caption(action);

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
    internal Task BackAsync()
    {
        // **A filter is cleared before anything is left** (specs/023 User Story 6, FR-020): Esc on a list showing only one
        // agent's conversations shows them all again, which is what the note under the list promises. Going back a view
        // with the filter still set would bring somebody back to a list quietly missing most of its rows.
        if (ConversationsOf is not null)
        {
            ConversationsOf = null;

            Queue(token => Current is { } project ? SelectAsync(project, token) : Task.CompletedTask);

            return Settled;
        }

        return _place.CanGoBack ? GoAsync(_place.Back()) : Task.CompletedTask;
    }

    /// <summary>The agent the conversations list is filtered to, or null when it shows every conversation.</summary>
    internal AgentName? ConversationsOf { get; private set; }

    /// <summary>Shows only the conversations one agent is part of, in the project on screen.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>When the list has been redrawn.</returns>
    /// <remarks>
    /// A filter on what the list already reads rather than a new broker call: a conversation's participants come with
    /// the page, so asking again for a narrower page would be a second answer to one question.
    /// </remarks>
    private Task ShowConversationsOf(AgentName agent)
    {
        ConversationsOf = agent;

        Queue(async token =>
        {
            if (Current is { } project)
            {
                await SelectAsync(project, token).ConfigureAwait(false);
            }

            Focus(Panes.ToList().IndexOf(Conversations));
        });

        return Settled;
    }

    /// <summary>Moves to a view and lays it out.</summary>
    /// <param name="place">Where to go.</param>
    /// <returns>When the view is on screen.</returns>
    /// <remarks>
    /// Internal rather than private so a test can put the surface on a view without first building
    /// a deployment to navigate through. One test still walks in the way a person does -- select,
    /// Enter -- because a suite that only ever arrived by this route could not tell that the route a
    /// person takes had stopped working.
    /// </remarks>
    internal async Task GoAsync(ViewPlace place)
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

        // **A conversation opens on its transcript, not on the room beside it.** The room is context; the
        // transcript is what somebody opened a conversation to read, and it is where every action on this
        // view already lived. Focusing the left column because it is drawn first would put the cursor on
        // the furniture.
        Focus(Opening(place));

        // **Arriving at a conversation reads it.** The place and the read used to be two acts, and the
        // conversations list happened to do both -- so any other route in left the room and the line
        // describing whatever was last read. Now the view cannot be on a conversation it has not read.
        //
        // **Awaited rather than queued, which the suite settled.** The first version queued it, and every
        // caller of this is already on the work queue -- so the read was appended behind the item that asked
        // for it, and a test awaiting `Settled` got a task that did not include it. The transcript was empty
        // where it had been filled, in a test about escape sequences that had nothing to do with any of this.
        if (place.Viewing == Viewing.Conversation && place.Title is { } conversation && Current is { } project)
        {
            await OpenAsync(project, conversation, Reading).ConfigureAwait(false);
        }

        // The same rule for settings: the view is never on screen showing values it has not read.
        if (place.Viewing == Viewing.Configuration)
        {
            await ReadConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>What the menu calls the way into a project's settings.</summary>
    internal const string ConfigurationTitle = "Configuration";

    /// <summary>The chord that opens a project's settings.</summary>
    internal static Key ConfigurationKey => Key.F3;

    /// <summary>Opens the settings of the project in view, or of the one selected in the list.</summary>
    /// <returns>When the view is on screen and read.</returns>
    private Task OpenConfiguration() =>
        (Current ?? Projects.Selected) is { } project
            ? GoAsync(ViewPlace.Configure(project.Name))
            : Task.CompletedTask;

    /// <summary>Reads the project's settings into the configuration view.</summary>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>When the view shows what the broker said.</returns>
    /// <remarks>
    /// One read per setting's source. There is one today, the facilitation report, which carries Auto Accept; a
    /// setting added later adds its row here, and the view needs nothing else.
    /// </remarks>
    private async Task ReadConfigurationAsync(CancellationToken cancellationToken)
    {
        if (Current is not { } project)
        {
            return;
        }

        var client = new BrokerClient(_directory.ClientFor(project));

        var read = await BrokerDirectory.ReadAsync(
            async () => await client.FacilitationAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        if (!read.Reachable)
        {
            Configuration.Unreadable(Safe.Text(read.Problem!));

            return;
        }

        Configuration.Show([AutoAccepting.AsSetting(read.Value!.AutoAccept)]);
    }

    /// <summary>Changes the setting under the cursor, through the door that setting has.</summary>
    /// <returns>When the door is open.</returns>
    private Task ChangeSetting() => Configuration.Selected?.Name switch
    {
        AutoAccepting.Title => AutoAccept(),
        _ => Task.CompletedTask,
    };

    /// <summary>Which pane a view opens focused on.</summary>
    /// <param name="place">The view being arrived at.</param>
    /// <returns>The pane index.</returns>
    /// <remarks>
    /// Every view but one opens on its first pane, which is also its only list. A conversation opens on its
    /// transcript, which is the second: the room is context beside it rather than the thing somebody came for.
    /// </remarks>
    private static int Opening(ViewPlace place) => place.Viewing == Viewing.Conversation ? 1 : 0;

    /// <summary>What the focused region can do, and the letter that does each.</summary>
    /// <returns>The actions, in the order the status line lists them.</returns>
    /// <remarks>
    /// Actions belong to the region that shows the thing acted on: a seat is taken from the agents
    /// region, a question is answered from a transcript, a project is created or removed from the
    /// projects region. That is User Story 2's rule, and having the region answer for its own
    /// actions is what keeps the status line and the keys from drifting apart.
    /// </remarks>
    internal IReadOnlyList<RegionAction> Actions() =>
        [.. Offered().Select(action => action with { Category = action.Category ?? Belongs() })];

    /// <summary>The menus the bar offers, left to right.</summary>
    /// <returns>The categories that have anything under them.</returns>
    /// <remarks>
    /// <para>
    /// <b>Alt and the title's first letter opens one</b>, which is what a menu bar does everywhere else — and
    /// it is free here, because every ordinary letter on this surface already belongs to an action or to
    /// the text somebody is typing. A bar that bound bare letters would take them from the regions. The
    /// letter is underlined on the bar, as it is in any other program's.
    /// </para>
    /// <para>
    /// <b>What is on it is the focused region's, plus what belongs to no region</b> (D2). It therefore
    /// changes as Tab moves, which is the point rather than a flicker: a menu offering <i>Withdraw…</i>
    /// while the projects pane has focus would have no agent to act on.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<string> BarTitles() => [.. MenuRow.Menus(Bar()).Select(menu => menu.Title)];

    /// <summary>Everything the bar reads: this region's actions, the message line's, and the global.</summary>
    /// <returns>The actions, in no particular order — <c>MenuRow</c> decides the order.</returns>
    /// <remarks>
    /// One list assembled in one place, so the bar and the keys that open its menus cannot disagree
    /// about what is on it. The three sources are the three that exist; a fourth would be added here.
    /// </remarks>
    private IReadOnlyList<RegionAction> Bar() => [.. Actions(), .. Messaging(), .. Global()];

    /// <summary>Runs what a menu line names, as the bar offers it now.</summary>
    /// <param name="stroke">The line's key.</param>
    /// <param name="label">The line's label, as written on the menu.</param>
    /// <remarks>
    /// Found again rather than carried by the line; <see cref="MenuBarView"/>'s constructor says why. A line
    /// that no longer matches anything runs nothing — the menu was built for a screen that has since
    /// changed, and guessing which action it meant is the one thing this must not do.
    /// </remarks>
    private void RunFromMenu(string stroke, string label)
    {
        var chosen = MenuRow.Menus(Bar())
            .SelectMany(menu => menu.Items)
            .FirstOrDefault(item => !item.Divides && item.Stroke == stroke && item.Label == label);

        if (chosen?.Action is { } action)
        {
            _ = action.Run();
        }
    }

    /// <summary>What can be done wherever the cursor is, rather than to what it is on.</summary>
    /// <returns>The actions no region owns.</returns>
    /// <remarks>
    /// <para>
    /// <b>A third source beside <see cref="Actions"/> and <see cref="Messaging"/>, and the reason is
    /// D2.</b> The bar is scoped to the focused region because an action belongs to the region showing
    /// the thing it acts on — but what the keys are, and which build this is, are about neither a
    /// region nor a selection. Scoping them would put Help under six different menus or under none.
    /// </para>
    /// <para>
    /// Both are already reachable: <c>?</c> opens the actions and the build is on the status line and
    /// the menu's own title. What this adds is that they are <em>named</em> where somebody looking for
    /// them would look, which is the whole complaint 021 exists to answer — the actions were
    /// discoverable only to somebody who already knew to press <c>?</c>.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<RegionAction> Global() =>
    [
        // **Back and quit, because the owner's table puts them on the bar** — back under View and quit
        // under Deployment. Both were already on the status line and in the `?` menu, and both are here
        // for the reason the bar exists: 0.18.4 records Escape being the only way out of a project view
        // for a whole release while nothing on the screen named it.
        //
        // They are not dispatched from `Global()`: Escape and the quit chord are answered further up,
        // where they were before. What this adds is that they are *named* in a second place, and running
        // them through the same delegates is what keeps the two from drifting.
        .. _place.CanGoBack
            ? new[]
            {
                new RegionAction(
                    default,
                    BackLabel ?? BackStroke,
                    BackAsync,
                    Category: ActionCategory.View,
                    Chord: Key.Esc,
                    Order: 1),
            }
            : Array.Empty<RegionAction>(),

        // **The way into a project's settings, from wherever a project is in view.** Global rather than a region's,
        // because inside a project the focused pane is the agents or the conversations, and a setting of the project
        // is neither. A chord rather than a letter, because a global letter is shadowed by whichever pane already
        // uses it -- named on the menu and dead under the cursor.
        .. _place.Viewing is not Viewing.Configuration and not Viewing.Conversation
           && (Current ?? Projects.Selected) is not null
            ? new[]
            {
                new RegionAction(
                    default,
                    ConfigurationTitle,
                    OpenConfiguration,
                    Category: ActionCategory.Project,
                    Chord: ConfigurationKey,
                    Order: 100),
            }
            : Array.Empty<RegionAction>(),

        new RegionAction(
            ActionsKey, KeysTitle, Offer, Category: ActionCategory.Help, Order: 1),

        new RegionAction(
            default, AboutTitle, About, Category: ActionCategory.Help, Chord: Key.F1, Order: 2),
    ];

    /// <summary>What the focused region's actions are about, unless one says otherwise.</summary>
    /// <returns>The category.</returns>
    /// <remarks>
    /// <para>
    /// <b>The region supplies the default and an action may override it.</b> 021's D1 says an action
    /// carries metadata guiding the UI, and this is how the metadata gets there: the region a thing is
    /// acted on from is nearly always what the thing is about, so stating it forty-five times would be
    /// forty-five chances to state it inconsistently. What is left is the handful the owner's own table
    /// files elsewhere — Health and the web UI belong to the deployment however they are reached — and
    /// those say so at the point they are built.
    /// </para>
    /// <para>
    /// The action still carries it once built, which is what the menu, the status line and the
    /// <c>?</c> dialog read. None of them asks a region anything.
    /// </para>
    /// </remarks>
    private ActionCategory Belongs() => Panes[FocusedRegion] switch
    {
        ProjectsRegion => ActionCategory.Project,
        AgentsRegion => ActionCategory.Agent,
        ConversationsRegion => ActionCategory.Conversation,
        TranscriptRegion => ActionCategory.Conversation,
        RoomRegion => ActionCategory.Conversation,
        LineRegion => ActionCategory.Conversation,
        ConfigurationRegion => ActionCategory.Project,
        _ => ActionCategory.View,
    };

    /// <summary>What the focused region offers, before a category is settled on any of it.</summary>
    /// <returns>The actions, in the order the status line lists them.</returns>
    private IReadOnlyList<RegionAction> Offered() => Panes[FocusedRegion] switch
    {
        // Naming the running deployment is offered only while there is one nothing names yet.
        // Offered always, it would invite somebody to name the same broker twice, which is two
        // projects sharing a store and is what §4.1 forbids.
        ProjectsRegion =>
        [
            new RegionAction('c', Naming.CreateTitle, Create),

            // **"Name the running deployment" was here and has gone.** It adopted a broker already
            // serving on a store and port taken from an autostart entry written with `--db` and
            // `--listen`. Neither flag exists, so no entry can be like that again, and a machine that
            // still has one needs its store *moved* rather than named where it lies — which is a
            // migration, with a broker stopped and agents sent away, not a form that pre-fills two
            // paths. Keeping it would have meant a second door onto that act, or a screen that quietly
            // pointed a named project at an empty database.

            new RegionAction('r', Removing.Title, Remove),

            // **Three of these belong to the deployment however they are reached**, which is the
            // owner's own table: health is read across every project, the log is the broker's, and the
            // web UI is another way into the whole thing. They are offered from this region because
            // this is where somebody is standing, and that is not the same as what they are about.
            new RegionAction('y', HealthTitle, ShowHealth, Category: ActionCategory.Deployment),
            new RegionAction('l', Files.ProjectTitle, ShowFiles),
            new RegionAction('p', Pruning.Title, PruneSelected),
            new RegionAction('g', FollowLogTitle, FollowLog, Category: ActionCategory.Deployment),

            // **Enrolling a folder of repositories moved to the agents pane**, on the owner's call of
            // 2026-09-27: a project is made first and agents are enrolled into it, so the act belongs where the
            // project is already the context rather than on a list where it has to be inferred from a cursor.
            new RegionAction('b', OpenConsoleTitle, OpenConsole, Category: ActionCategory.Deployment),

            // **Auto Accept was here, as a bare `a`, and the owner read it as "Auto-Accept what??"** A switch named
            // with no subject, beside creating and removing projects. It lives in the configuration view now, which
            // says what it decides before anybody changes it; that view is reached from `Global()`, so the Project
            // menu offers it from inside a project as well as from this list.
        ],

        AgentsRegion =>
        [
            // Enter's meaning here, named where the others are (specs/023 T066): a key the surface takes and does not
            // name is one nobody finds. Offered only on a row, since that is what it acts on.
            .. Agents.Selected is not null
                ? new[] { new RegionAction(default, RowMenuTitle, RowMenu, Chord: Key.Enter) }
                : Array.Empty<RegionAction>(),

            new RegionAction('a', Adding.Title, Add),

            // Beside adding one, because it is the same act for many: the two ways a project gains agents
            // belong on one menu. `f` for the folder it asks for.
            new RegionAction('f', Onboarding.Title, Enrol),

            // Offered only while there is something to start, so the line never names a key that
            // would do nothing — and, on a deployment where every agent has registered, never
            // appears at all.
            .. NeverRegistered() is { Count: > 0 } waiting
                ? new[]
                {
                    new RegionAction('u', StartEveryTitle(waiting.Count), StartEvery),
                }
                : Array.Empty<RegionAction>(),

            // Never for a seat, whoever holds it: a person leaves a seat, they are not retired
            // (research R7). Withdrawing refuses one reached by any other route as well.
            .. Agents.Selected is { Seat: false } chosen
                ? chosen.State.Contains("retired", StringComparison.Ordinal)
                    ? [new RegionAction('i', Withdrawing.ReinstateTitle, Reinstate)]
                    : new[] { new RegionAction('r', Withdrawing.Title, Withdraw) }
                : Array.Empty<RegionAction>(),

            // What the operator told this agent, and what it said back. Never for a seat: a seat
            // is a person, and nobody instructs themselves.
            .. Agents.Selected is { Seat: false } told
                ? new[]
                {
                    // `h`, for history. It was `w`, which is taking delivery wherever a seat is held -- so
                    // with a seat held and an agent selected this pane named one key twice, and the key ran
                    // whichever came first. `NoTwoActionsShareAKeyTests` holds it.
                    new RegionAction('h', ToldTitle + told.Name, ShowTold),
                    new RegionAction('o', Instructing.Title + told.Name, InstructSelected),

                    // Never for a seat either, and for the same reason: an ownership line is how
                    // peers decide who to send work to, and a person holding a seat is not somebody
                    // work is routed to by a roster entry.
                    new RegionAction('n', Ownership.Title + ": " + told.Name, OwnershipOfSelected),

                    // **Asking is offered whether or not a seat is held, and that is deliberate.**
                    // An ask is a message and needs one, but hiding the action until somebody has a
                    // seat is 0.28.2's recorded defect: the owner could not find how to start a
                    // conversation and reached for an instruction, which is not a message at all.
                    // So it is always here and says what is missing when it is pressed.
                    //
                    // `k`, as in ask. It was `a`, which is adding an agent, first in this list -- so the key
                    // always added and the ask could be reached only from the menu.
                    new RegionAction(
                        'k',
                        Ownership.AskTitle + ": " + told.Name,
                        AskWhatItOwns,
                        Owing.Requesting),
                }
                : Array.Empty<RegionAction>(),

            // **Offered while there is anybody to ask, and named with the count**, because it launches
            // one exchange per agent and a person deciding is deciding about that number. Offered
            // without a seat for the reason the single ask is: an action nobody can find is
            // indistinguishable from one that does not exist.
            .. Agents.Registry.Count > 0
                ? new[]
                {
                    new RegionAction(
                        'e',
                        Ownership.AskEveryTitle,
                        AskEveryoneWhatTheyOwn,
                        Owing.Requesting),

                    // **The review is what makes Auto Accept safe to leave on** (the owner's D3): every line, where it
                    // came from, and whatever Parley is waiting on, with the decisions about each. `v`, as in review;
                    // offered whenever there is anybody to review and needing no seat, because nothing in it speaks as
                    // the person.
                    new RegionAction('v', OwnershipReview.Title, ReviewOwnership),
                }
                : Array.Empty<RegionAction>(),

            // **No join and no leave** (specs/023). The broker provides the person's seat in every project and this
            // terminal takes it on its own; there is nothing to join, and closing the terminal is how a person leaves.

            // **Taking delivery lives here as well as in a transcript, and that is the correction.** It
            // was offered only inside a conversation, so collecting what you had been sent meant opening
            // one -- and what is waiting is frequently in a conversation you have never seen, which is
            // exactly the one you would not think to open. This pane is where the seat is.
            .. Held is not null
                ? new[] { new RegionAction('w', Participation.DeliveryTitle, TakeDelivery) }
                : Array.Empty<RegionAction>(),
        ],

        // **The room column.** A list is somewhere a letter is free — which the box beside it is not, since every
        // letter there belongs to the text. No action on this surface exists only as a key.
        RoomRegion =>
        [
            // Offered only on a row that is a participant, because that is the only row it would do anything
            // to -- and a key named where it does nothing is the fault this surface keeps a rule about.
            .. Held is not null && Room.SelectedPeer is not null
                ? new[] { new RegionAction(default, IncludeTitle, ToggleOne, null, Key.Space) }
                : Array.Empty<RegionAction>(),
        ],

        // Offered only on a row, so the key is never named where it would change nothing. Enter does the same, taken
        // in `OnKeyDown`, because Enter on a setting is what somebody presses first.
        ConfigurationRegion =>
        [
            .. Configuration.Selected is not null
                ? new[] { new RegionAction('c', ConfigurationRegion.ChangeTitle, ChangeSetting) }
                : Array.Empty<RegionAction>(),
        ],

        LineRegion =>
        [
            .. Held is not null
                ? new[]
                {
                    new RegionAction('\r', SendTitle, SendLine, Owing.Requesting),
                    new RegionAction(default, BreakTitle, BreakLine, BreakAlso, Key.Enter.WithShift),
                }
                : Array.Empty<RegionAction>(),
        ],

        // The conversations half of a project view. Sorting and the archived toggle are view
        // state; archiving is reversible and asks nothing; destroying is behind the full door.
        ConversationsRegion =>
        [
            new RegionAction('s', SortTitle, SortConversations),
            new RegionAction('v', _showArchived ? HideArchivedTitle : ShowArchivedTitle, ToggleArchived),

            // Starting one belongs here rather than in a transcript of a different one, which is this method's own
            // rule read straight. **Offered whenever this terminal can speak as the person at all** (specs/023): there
            // is no seat to take first any more, so there is no second spelling of this action that says so.
            .. Seats is not null
                ? new[] { new RegionAction('k', Participation.StartTitle, Start, Owing.Requesting) }
                : Array.Empty<RegionAction>(),

            .. Conversations.Selected is { } chosen
                ? new[]
                {
                    new RegionAction('h', chosen.Archived ? UnarchiveTitle : ArchiveTitle, ArchiveSelected),
                    new RegionAction('l', Files.ConversationTitle, ShowConversationFiles),
                    new RegionAction('f', Closing.Title, CloseSelected),
                    new RegionAction('d', Destroying.Title, DestroySelected),
                }
                : Array.Empty<RegionAction>(),

            // The deployment's, as on the projects region and for the same reason.
            new RegionAction('y', HealthTitle, ShowHealth, Category: ActionCategory.Deployment),
            new RegionAction('b', OpenConsoleTitle, OpenConsole, Category: ActionCategory.Deployment),
        ],

        // Taking part needs a seat, because every one of these speaks as somebody. Without one the
        // region still shows a transcript; it simply has no voice to use in it.
        //
        // **Copying does not need one**, which is why this arm is no longer gated on a seat as a
        // whole: reading a conversation and taking a copy of what you read are the same act, and a
        // person with no seat is exactly the operator most likely to be quoting a transcript
        // somewhere else.
        TranscriptRegion =>
        [
            .. Held is not null
                ? new[]
                {
                    new RegionAction('w', Participation.DeliveryTitle, TakeDelivery),
                    new RegionAction('a', Participation.ResultTitle, SendResult),
                    new RegionAction('q', Participation.ClarifyTitle, Clarify),
                    new RegionAction('k', Participation.RequestTitle, SendRequest, Owing.Requesting),
                }
                : Array.Empty<RegionAction>(),

            .. Transcript.Open is not null
                ? new[]
                {
                    // **What is on the screen rather than what is in the deployment.** Copying a
                    // transcript and folding one change nothing anybody else can see, which is the
                    // line the View category draws.
                    new RegionAction(CopyKey, CopyTitle, Copy, Category: ActionCategory.View),
                    new RegionAction(CopyMessageKey, CopyMessageTitle, CopyMessage, Category: ActionCategory.View),
                    new RegionAction(
                        FoldKey,
                        Transcript.Folded ? UnfoldTitle : FoldTitle,
                        Fold,
                        Category: ActionCategory.View),
                }
                : Array.Empty<RegionAction>(),

            // **Recognised by the conversation's identifier and never by anything in a payload.**
            // §13 tells a receiver a payload is inert data, so an ownership discussion is known by
            // the name it was opened under -- which is metadata the protocol already carries.
            // `n` is what it is in the agents pane, because it is the same operation reached from
            // the other end: there the row is the agent, here the conversation is.
            //
            // Needs no seat. Accepting is an operator operation and reading the transcript is how
            // somebody decides to, which is the same reasoning that ungated copying.
            .. OwnershipAsk.Recognises(Transcript.Open)
                ? new[]
                {
                    // **The agent's**, though it is reached from a transcript: what it decides is what
                    // one agent's peers will read, and the conversation is only where it was agreed.
                    new RegionAction(
                        'n', Ownership.AcceptTitle, AcceptProposed, Category: ActionCategory.Agent),
                }
                : Array.Empty<RegionAction>(),
        ],

        _ => [],
    };

    /// <summary>What Enter on an agents-list row is called, on the status line and in the menu.</summary>
    internal const string RowMenuTitle = "What can be done with this row";

    /// <summary>What the row menu offers on the person's own row to see their conversations.</summary>
    internal const string YourConversationsTitle = "Your conversations";

    /// <summary>What the row menu offers on an agent's row to start a conversation with it, before the name.</summary>
    internal const string StartWithTitle = "Start a conversation with ";

    /// <summary>What the row menu offers on an agent's row to see its conversations.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The label.</returns>
    internal static string ConversationsOfTitle(string agent) => $"Conversations {agent} is in";

    /// <summary>What the row menu offers on an agent that is not running, before the name.</summary>
    private const string StartThisTitle = "Start ";

    /// <summary>
    /// Opens what can be done with the row under the cursor in the agents list (specs/023 User Story 6).
    /// </summary>
    /// <returns>When the menu is open.</returns>
    /// <remarks>
    /// <para>
    /// <b>The owner's report of 0.36.0</b>: "it felt more natural to be in the agents section and hit enter on my user in
    /// order to start a conversation." Enter on a seat row used to <i>take that seat</i>, which went with the typed name,
    /// so the key needed a meaning anyway — and a row is where somebody already is when they want to act on it.
    /// </para>
    /// <para>
    /// <b>Built from the action table rather than a list of its own</b>, where an action already exists: a label written
    /// twice is one that can differ between this menu, the status line and <c>?</c>. Two entries are new because nothing
    /// offered them — a conversation with this agent, and the list filtered to its conversations.
    /// </para>
    /// <para>
    /// <b>Retire, reinstate and purge are never here</b> (FR-019), on the owner's call: they stay in the Agent menu and on
    /// their own keys, behind the typed name, so a row menu opened by the most ordinary key there is can never be one
    /// arrow away from withdrawing anybody. Anything that cannot be done right now is left out rather than offered and
    /// then refused (User Story 6, scenario 4).
    /// </para>
    /// </remarks>
    private Task RowMenu()
    {
        if (App is not { } app || Agents.Selected is not { } row || !AgentName.TryCreate(row.Name, out var name))
        {
            return Task.CompletedTask;
        }

        var entries = new List<(string Label, Func<Task> Run)>();

        if (PersonSeat.Is(name))
        {
            if (Seats is not null)
            {
                entries.Add((Participation.StartTitle, Start));
            }

            entries.Add((YourConversationsTitle, () => ShowConversationsOf(name)));

            if (Held is not null)
            {
                entries.Add((Participation.DeliveryTitle, TakeDelivery));
            }
        }
        else if (!row.Seat)
        {
            if (Seats is not null)
            {
                entries.Add((StartWithTitle + name.Value, () => Compose(inConversation: null, to: name)));
            }

            entries.Add((ConversationsOfTitle(name.Value), () => ShowConversationsOf(name)));

            // The agent's own actions as the table already offers them — ask what it owns, review its line, instruct
            // it, what it was told — and nothing that withdraws it.
            entries.AddRange(Offered()
                .Where(action => action.Key is 'k' or 'n' or 'o' or 'h')
                .Select(action => (action.Label, action.Run)));

            if (Agents.SelectedStanding is { Running: false, Retired: false, StartableHere: true })
            {
                entries.Add((StartThisTitle + name.Value, StartSelected));
            }
        }

        if (entries.Count == 0)
        {
            return Task.CompletedTask;
        }

        var menu = Prompting.Choose(row.Name, "What to do with this row.", [.. entries.Select(e => e.Label)]);
        var token = Prompting.Open(app, menu);

        menu.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (entries.FirstOrDefault(e => e.Label == chosen) is { Run: { } run })
            {
                _ = run();
            }
        };

        return Task.CompletedTask;
    }

    /// <summary>Asks this agent's machine to start it, from its row.</summary>
    /// <returns>When the request has been made and its outcome said.</returns>
    /// <remarks>
    /// The same operator operation the add screen and "start every agent" use (§10.2 <c>start</c>). It says what was
    /// asked for rather than claiming the agent is running: whether it then attaches is a different question, and 0.25.2
    /// records a screen predicting a registration instead of checking one.
    /// </remarks>
    private Task StartSelected()
    {
        if (Current is not { } project || Agents.Selected is not { } row || !AgentName.TryCreate(row.Name, out var agent))
        {
            return Task.CompletedTask;
        }

        Queue(async token =>
        {
            var started = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(_directory.ClientFor(project))
                    .StartAsync(agent, "a person started it from its row", token).ConfigureAwait(false))
                .ConfigureAwait(false);

            Said = started.Problem ?? $"Asked {agent.Value}'s machine to start it.";

            await SelectAsync(project, token).ConfigureAwait(false);
        });

        return Settled;
    }

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

        // The message's actions are in the menu for the reason back and quit are: no action on this
        // surface exists only as a key, and a status line too narrow to list them has taken their
        // captions off the screen.
        var about = Messaging();

        var menu = Prompting.Choose(
            ActionsTitle,
            // The build beside the pane's name, for the narrow terminal whose status line has already dropped it.
            $"{NameOf(Panes[FocusedRegion])} · {VersionLabel}",
            [
                .. back is null ? [] : new[] { back },
                .. about.Select(Detailed),
                .. actions.Select(Detailed),
                QuitLabel,
            ]);

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

            if (about.Concat(actions).FirstOrDefault(a => Detailed(a) == chosen) is { } taken)
            {
                _ = taken.Run();
            }
        };

        return Task.CompletedTask;
    }

    /// <summary>The seat this person holds, if they hold one.</summary>
    /// <remarks>
    /// **Letting go of a seat drops its count with it**, here rather than at each of the three places a seat is
    /// set. One of them forgot, and a driven test caught the result: the number stayed on the status line after
    /// the seat had gone, which is somebody else's mail under nobody's name. A property that cannot be set
    /// without the count following cannot forget.
    /// </remarks>
    internal SeatHold? Held
    {
        get;

        private set
        {
            field = value;

            // **The draft is kept.** It is who a line goes to in this conversation, read from the conversation, and the
            // seat it is drafted as is the broker's and outlives a dropped stream (specs/023). Dropping it here was right
            // while a seat went with its stream; after that it left a person back from the idle window holding a seat and
            // no draft, and their first Enter refused (the owner's report of 0.36.0, `SendAfterIdleTests`).
            if (value is null)
            {
                Mail = null;
            }

            // The line says what it needs the moment the seat changes, rather than at the next read. A person
            // who has just lost a seat must not be looking at a preview of what Enter would have done.
            Line?.Show(Drafting, field is not null);
        }
    }

    /// <summary>
    /// Keeps the person marked here while somebody is at the terminal, and lets the broker read them as away when
    /// nobody has been.
    /// </summary>
    /// <param name="now">The current instant.</param>
    /// <remarks>
    /// <para>
    /// <b>Called on every draw, and that is what makes the idle rule apply at all.</b> Without a hook the stream
    /// would be dropped only when the process ended, and a terminal left open overnight would mark the person here
    /// all night — the false presence <c>SeatBook.IdleAfter</c> exists to prevent.
    /// </para>
    /// <para>
    /// <b>Nothing is said when the stream is dropped, because nothing was taken from the person</b> (specs/023). This
    /// used to announce "your seat was released" and forget who the person was, since the seat went with the stream.
    /// The broker never releases the seat now: an agent that asks meanwhile is told the person has it, the mail waits,
    /// and the next key takes the stream back (<see cref="Touched"/>).
    /// </para>
    /// <para>
    /// The touch is dated from the last key or click, not from the draw: a terminal redraws whether or not anybody is
    /// at it, and a touch taken at the moment of drawing would keep an abandoned terminal here for ever.
    /// </para>
    /// </remarks>
    internal void Tick(DateTimeOffset now)
    {
        // **`Current` rather than `Projects.Selected`.** Two readers of "which project is this" on different
        // accessors is how they come to disagree, which is this repository's most-repeated defect and the whole
        // argument for `Current` existing.
        if (Seats is null || Current is not { } project)
        {
            return;
        }

        Seats.Touch(_touched);
        Seats.Sweep(now);

        var was = Held;
        Held = Seats.Held(project.Name.Value);

        // The count belongs to the seat, so the watch starts with one; setting Held drops it again.
        if (Held is not null && was is null)
        {
            WatchMail();
        }
    }

    /// <summary>
    /// Takes the person's seat in a project if nothing holds it now, and says why when it cannot be taken.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="registry">Its registry, when it has just been read; null to read it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>When the seat is held, or its absence has been said.</returns>
    /// <remarks>
    /// <para>
    /// <b>No name is asked for and none is typed</b> (specs/023). The broker provides the person seat in every project
    /// it serves, and this attaches as the row its registry names. It runs when the terminal opens, for every project,
    /// and again on the next key after an idle drop.
    /// </para>
    /// <para>
    /// <b>A broker that did not answer is not reported here</b>: the agents region already says so, and saying it twice
    /// on every refresh would bury whatever the person did say. What is reported is the one case nothing else shows —
    /// a broker that answered and has no seat for a person, which is one older than 0.36.0 — and only when it changes.
    /// </para>
    /// </remarks>
    private async Task EnsureSeatAsync(
        ProjectSettings project, IReadOnlyList<AgentRecord>? registry, CancellationToken cancellationToken)
    {
        if (Seats is null)
        {
            return;
        }

        var outcome = await Seats.EnsureAsync(
            project,
            async token => registry ?? (await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(_directory.ClientFor(project))
                    .RegistryAsync(token).ConfigureAwait(false)).ConfigureAwait(false)).Value,
            cancellationToken).ConfigureAwait(false);

        if (outcome.Problem is { } problem && outcome.Problem != _seatProblem && registry is not null)
        {
            Said = problem;
        }

        _seatProblem = outcome.Problem;

        if (Current is { } current && current.Name == project.Name)
        {
            var was = Held;
            Held = outcome.Hold;

            if (Held is not null && was is null)
            {
                WatchMail();
            }
        }
    }

    /// <summary>What is said when something would be sent and this terminal is not connected as the person.</summary>
    /// <remarks>
    /// The key that asked has already started reconnecting (<see cref="Touched"/>), so the sentence can promise that a
    /// moment will do. When the broker is down or too old, the agents region and the message line say why.
    /// </remarks>
    internal const string NotConnected =
        "Not connected to this project as you just now, so nothing was sent. It is reconnecting; try again in a moment.";

    /// <summary>Said when Enter is pressed before the conversation has been read, so there is nobody to send to yet.</summary>
    /// <remarks>
    /// The send asks for the read as it says this, so the sentence is a promise the surface is keeping, not a guess.
    /// </remarks>
    private const string NotReadYet =
        "This conversation is still being read, so nothing was sent. Press Enter again in a moment.";

    /// <summary>Said when somebody joined or left since the preview was drawn, so the line would not go where it said.</summary>
    /// <remarks>Most often met on coming back from being away, when the preview was drawn before the person left.</remarks>
    private const string RecipientsChangedWhileAway =
        "Who this goes to changed since it was shown, so nothing was sent. Check who it goes to above, then press " +
        "Enter again.";

    /// <summary>The last reason a seat could not be taken, so the same one is not said again on every refresh.</summary>
    private string? _seatProblem;

    /// <summary>Whether a seat is already being taken again after an idle drop, so a burst of keys asks once.</summary>
    private int _retaking;

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

    /// <summary>
    /// This machine's node catalogue, or null to work it out from the manifest.
    /// </summary>
    /// <remarks>
    /// <b>Passed in rather than read from the environment, and that is a scar.</b> The first version
    /// asked <c>PARLEY_NODE_CONFIG</c> for itself, deep inside the write — the node reads that variable,
    /// so this has to agree with it or an agent is enrolled into a file nothing starts it from. But the
    /// terminal's tests build this surface in process, so they inherited the developer's own setting and
    /// every one of them enrolled its throwaway repository into the live deployment's catalogue. Thirty-five
    /// junk entries went into a real machine's file before anybody noticed.
    /// <para>
    /// <c>ToolProcess</c> clears those variables before launching the tool as a child, which is the same
    /// guard — it just could not cover a surface that never launches anything. So this joins
    /// <see cref="Opener"/> and <see cref="Copier"/>: resolved once by whoever builds the shell, and
    /// replaceable by a test. Reading ambient state from the bottom of a call stack is what made it
    /// unreachable from the top.
    /// </para>
    /// </remarks>
    internal string? CataloguePath { get; init; }

    /// <summary>Who the line in each conversation goes to, kept across restarts.</summary>
    /// <remarks>
    /// For this run only unless whoever builds the shell names a file, for the reason <see cref="CataloguePath"/> is
    /// passed in: a driven test must never write into a real machine's Parley folder.
    /// </remarks>
    internal AddressBook Addressing { get; init; } = new(null);

    /// <summary>The project and conversation the current draft was built for.</summary>
    /// <remarks>
    /// <b>A draft belongs to one conversation, and nothing said so until 2026-10-02.</b> Rebuilding the draft carried the
    /// previous one's narrowing forward whatever conversation it had been for, so narrowing to one agent in one
    /// conversation and then opening another addressed the second conversation's line to that agent — and sending it
    /// would have pulled the agent into a conversation it was not in. Found while making the narrowing survive a restart.
    /// </remarks>
    private (string Project, string Conversation)? _drafted;

    /// <summary>The stream the draft was derived through, so a send can tell a draft from before the person was away.</summary>
    /// <remarks>
    /// While away there is no stream, so the follow leaves the draft alone; coming back takes a new one. A draft whose
    /// stream is not the one held now says who owed whom before the person left (the review gate's finding).
    /// </remarks>
    private SeatHold? _draftedWith;

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
    /// <summary>Opens the flow that makes a folder of repositories into agents.</summary>
    /// <returns>When the first dialog is open.</returns>
    /// <remarks>
    /// <b><see cref="Current"/> rather than the projects region's selection</b>, for the reason
    /// <see cref="Add"/> and <see cref="Withdraw"/> both say: a project view gives another region
    /// the screen, so the projects list may be off screen and never filled, and the action would
    /// run having silently acted on nothing. It is the fourth place that mistake has been made here.
    ///
    /// <para>
    /// The folder browsed to is remembered in <see cref="_lastAdded"/>, the same field adding one
    /// repository uses. They are the same question asked twice — which folder holds the checkouts —
    /// and two memories of it would disagree the first time somebody used both.
    /// </para>
    /// </remarks>
    private Task Enrol() =>
        App is { } app
            ? Onboarding.OpenAsync(
                app,
                Current,
                Projects.Projects,
                ManifestPath,
                StartedIn,
                _lastAdded,
                folder => _lastAdded = folder,
                Queue,

                // The activities come off the rows before the registry is read again, so the list drawn next
                // says where each agent landed rather than what it was last doing.
                async said =>
                {
                    Agents.ClearActivity();

                    await Reporting(said).ConfigureAwait(false);
                },

                // **Shown on each agent's row while it happens**, because each agent is started and waited for
                // in turn and a screen that says nothing for a minute is one somebody assumes has hung -- the
                // owner's report of 2026-09-27. On the row rather than the message line, their call of
                // 2026-09-28. The catalogue is read again each time because an agent being enrolled was not in
                // it when the rows were last drawn.
                (role, doing) => Agents.Activity(role, doing, Catalogue()))
            : Task.CompletedTask;

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

    /// <summary>Opens placed files and folders on this machine (SPEC.md §10.2).</summary>
    /// <remarks>
    /// A seam, for exactly the reason <see cref="Opener"/> is one: a suite that pressed Open would launch an editor on
    /// the machine of whoever ran it. <c>Driving.Show</c> replaces it on every driven surface.
    /// </remarks>
    internal Parley.Client.Files.LocalLauncher FileLauncher { get; set; } = Parley.Client.Files.LocalLauncher.ForThisMachine;

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
    /// <b>It is bound here because the toolkit binds it nowhere.</b> <c>TerminalCommand</c> said in a
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

    /// <summary>Which build this is, as the status line and the actions menu say it.</summary>
    /// <remarks>
    /// This tool's own version, not the broker's: they are installed apart and can differ, and the broker's
    /// is in its log and in <c>parley --version</c>.
    /// </remarks>
    internal static string VersionLabel { get; } = $"parley-terminal {BuildIdentity.Version}";

    /// <summary>What the menu calls the list of everything the focused region can do.</summary>
    internal const string KeysTitle = "Keys: what this region can do";

    /// <summary>What the menu calls the build.</summary>
    internal const string AboutTitle = "About this build";

    /// <summary>Says which build this is and which protocol it speaks.</summary>
    /// <returns>When the dialog is open.</returns>
    /// <remarks>
    /// <b>The tool's own build and never the broker's.</b> `parley-terminal` is installed apart from
    /// the broker, so naming the broker's version here would be this program reporting somebody else's
    /// — which 0.28.1 records both surfaces being corrected for. The protocol is the one this build
    /// speaks; whether the broker agrees is what an attach settles, and it says so if it does not.
    /// </remarks>
    private Task About()
    {
        if (App is not { } app)
        {
            return Task.CompletedTask;
        }

        var saying = Prompting.Choose(
            AboutTitle,
            $"""
             {VersionLabel}

             Protocol {ProtocolVersion.Current}, spoken by this tool. A broker that speaks another
             refuses an attach and names both, rather than half-working.

             The broker and the web UI are installed separately and may be other versions.
             """,
            [CloseLabel]);

        var open = Prompting.Open(app, saying);

        saying.Done += (_, _) => Prompting.Close(app, open);

        return Task.CompletedTask;
    }

    /// <summary>The one answer a dialog that only tells somebody something needs.</summary>
    /// <remarks>
    /// Private, because <see cref="About"/> is the only dialog here that tells rather than asks, and
    /// <c>DeadSurfaceTests</c> is right to say that <c>internal</c> claims somebody else needs it. If a
    /// second such dialog appears this moves rather than being widened on the guess that one will.
    /// </remarks>
    private const string CloseLabel = "Close";

    /// <summary>Where the web console listens when a broker hosts it.</summary>
    /// <remarks>
    /// <b>The one source of the number, not a second copy of it.</b> This was a literal
    /// until it was noticed that the literal was <c>5045</c> and every other statement of the
    /// default — the host, the usage text, the address an autostart entry registers — said
    /// <c>5280</c>. So the one action on this surface that hands off to a browser opened a port
    /// nothing listens on, on every machine, and said <c>Opened …</c> while doing it. A browser
    /// that starts and lands nowhere is the failure <see cref="Launch.Open"/> cannot report,
    /// because from here it succeeded.
    /// </remarks>
    private const string ConsoleAddress = WebUiAddress.Loopback;

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
    /// <remarks>
    /// <b>Setting this draws it.</b> The property and the label are one thing on purpose: the
    /// defect it replaced was twenty callers setting a string that reached no screen, and a design
    /// where a caller has to remember a second step is that defect waiting to happen again.
    /// </remarks>
    internal string Said
    {
        get;

        private set
        {
            field = value;

            // Neutralised on the way to the screen, not on the way in. Some of these sentences are
            // this surface's own words and some are a broker's -- `The broker refused the join:`
            // ends with whatever came back from the far end of a gRPC call -- and a message line is
            // as good a place to smuggle an escape sequence as a transcript is.
            _said = Safe.Text(value);

            Show();
        }
    } = string.Empty;

    /// <summary>Whether what is being said is longer than the line can hold.</summary>
    internal bool SaidMore { get; private set; }

    /// <summary>The dialog showing the whole message, once one has been opened.</summary>
    internal Saying? Showing { get; private set; }

    /// <summary>The message as it is drawn, one entry per row.</summary>
    /// <remarks>
    /// What a test reads to find out whether a sentence reached the screen whole.
    /// <see cref="Said"/> is the string that was set; this is what the person can see of it.
    /// </remarks>
    internal IReadOnlyList<string> Shown =>
        _message.Text.ReplaceLineEndings("\n").Split('\n');

    /// <summary>
    /// Draws the message at whatever height it needs, and sizes the panes to what is left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Wrapped here rather than by the label.</b> The breaks are then this surface's own, which
    /// is the same bargain <see cref="Safe.Lines"/> strikes — and the row count is known rather
    /// than inferred, which is what the panes above have to be sized against. A label left to wrap
    /// itself is a label whose height nothing can agree with.
    /// </para>
    /// <para>
    /// <b>Bounded, and it says when it is bounding.</b> The last row names the key that shows the
    /// rest, because the rule this surface is built on is that nothing is silently cut — the
    /// status line drops whole captions rather than clipping one, and the transcript wraps rather
    /// than trimming. A message with more to it than fits is the same problem arriving in the one
    /// row that exists to report problems.
    /// </para>
    /// <para>
    /// Laid out under <see cref="_screen"/> like everything else that changes the arrangement: this
    /// is reached from queued work on a pool thread, and <c>ArrangeLocked</c>'s own remark records
    /// what the main loop is doing to the same views at the same time.
    /// </para>
    /// </remarks>
    private void Show()
    {
        var width = Viewport.Width > 0 ? Viewport.Width : Threshold;
        var height = Viewport.Height > 0 ? Viewport.Height : 24;
        // **The bar's row is subtracted here as well, and leaving it out was a real regression.** This
        // read `height - 1 - LeastRegionRows`, counting the status line and not the row 021 put on top —
        // so on a short terminal the message line grew into the floor the regions are promised, and
        // `On_a_short_terminal_the_regions_keep_their_floor` caught it. The floor is the thing being
        // protected; whichever chrome is added, it comes out of the message's room and not out of that.
        var room = Math.Clamp(height - BarRows - 1 - LeastRegionRows, 1, MessageDeepest);

        IReadOnlyList<string> pieces = _said.Length == 0
            ? [string.Empty]
            : TranscriptRegion.Wrap(_said, width);

        SaidMore = pieces.Count > room;

        IReadOnlyList<string> shown = SaidMore
            ? [.. pieces.Take(room - 1), $"{Alt(WholeKey)}: {WholeTitle}"]
            : pieces;

        _message.Text = string.Join(Environment.NewLine, shown);

        lock (_screen)
        {
            if (shown.Count != _messageRows)
            {
                _messageRows = shown.Count;
                _message.Y = Pos.AnchorEnd(1 + _messageRows);
                _message.Height = _messageRows;

                // The heights alone, rather than a whole rearrangement: the panes on screen are
                // the right panes already, and `ArrangeLocked` ends by taking focus — which a
                // message must not do, since one can arrive while somebody is typing in a dialog.
                HeightsLocked();
            }

            // The captions change with the message: clearing is offered only while there is
            // something to clear, and the whole of it only while the line is holding part of it.
            DescribeLocked();
        }
    }

    /// <summary>Takes the message off the screen and gives its rows back.</summary>
    /// <returns>When it has gone.</returns>
    /// <remarks>
    /// Asked for by the owner in the same breath as the line being too short: a report that has
    /// been read is a report occupying rows somebody wants back. Nothing else clears it — a
    /// message stands until the next one replaces it or this removes it, because a line that faded
    /// on a timer would take the one record of what happened with it.
    /// </remarks>
    internal Task Clear()
    {
        Said = string.Empty;

        return Task.CompletedTask;
    }

    /// <summary>Shows a message the line could not hold, whole.</summary>
    /// <returns>When the dialog is open.</returns>
    internal Task Whole()
    {
        if (App is not { } app || _said.Length == 0)
        {
            return Task.CompletedTask;
        }

        // The session token is dropped and Escape closes it, exactly as `Telling` does: a dialog
        // that only shows something has one way out and no decision to take.
        Showing = Saying.Open(
            app, _said, Viewport.Width > 0 ? Viewport.Width - 4 : Threshold).View;

        return Task.CompletedTask;
    }

    /// <summary>Puts a sentence on the message line.</summary>
    /// <param name="said">What to say.</param>
    /// <remarks>
    /// A seam, like <see cref="Opener"/>, and for the same kind of reason: about half of what this
    /// line ever shows is a broker's words rather than this surface's, and there is no way to make
    /// a real broker say a chosen thing. What has to be checked is that whatever it says is
    /// neutralised on the way to the screen, and that needs a caller who can choose the words.
    /// </remarks>
    internal void Report(string said) => Said = said;

    /// <summary>Opens the dialog that makes a repository an agent of this project.</summary>
    /// <returns>When the dialog is open.</returns>
    /// <remarks>
    /// <b><see cref="Current"/> rather than the projects region's selection, for the reason
    /// <see cref="Withdraw"/> says at more length.</b> Both were found in one sweep and both are the
    /// same defect: a project view gives this region the screen, so the projects list is off screen
    /// and may never have been filled, and the action returned having done nothing while the status
    /// line went on offering it.
    /// </remarks>
    internal Task Add() =>
        App is { } app && Current is { } project
            ? Adding.OpenAsync(
                app,
                project,
                new Catalogue(ManifestPath, CataloguePath),
                // **The project is a parameter rather than the one on screen**, because the start
                // does not always go to it: a repository another project already starts unattended
                // is started through that one, which is the only node that names it.
                async (asked, agent, token) => await BrokerDirectory.ReadAsync(async () =>
                    await new BrokerClient(_directory.ClientFor(asked))
                        .StartAsync(agent, "it was just added", token).ConfigureAwait(false))
                    .ConfigureAwait(false),
                RegisteredIn,
                Queue,
                async said =>
            {
                Said = said;

                await SelectAsync(project, _reading.Token).ConfigureAwait(false);
            },
                _lastAdded,
                workspace => _lastAdded = workspace,
                StartedIn)
            : Task.CompletedTask;

    /// <summary>Puts the open conversation on the clipboard.</summary>
    /// <returns>When it has been tried.</returns>
    /// <remarks>
    /// <b>What is on the screen, rather than what the export writes.</b> The two differ —
    /// <c>Transcript.Write</c> flattens every payload onto one line, because a text file has no
    /// defence against a payload shaped like an annotation — and this surface has a defence: the
    /// indent, which every line but a header carries. So a copy of what somebody is looking at is
    /// both the more readable artefact and a safe one, and it is what they asked for when they
    /// asked to copy the conversation.
    /// </remarks>
    internal Task Copy()
    {
        if (App is not { } app || Transcript.Open is null)
        {
            return Task.CompletedTask;
        }

        var written = Transcript.Written;

        Said = Copier(app, written) ? Copied(written.Length) : NotCopied;

        return Task.CompletedTask;
    }

    /// <summary>Puts the one message under the cursor on the clipboard.</summary>
    /// <returns>When it has been copied, or why not has been said.</returns>
    /// <remarks>
    /// Offered whenever a conversation is open rather than only while the cursor is on a message, because the cursor moves
    /// without the actions being read again. A cursor on no message — the blank line between two, or the conversation's
    /// own preamble — is told so rather than handed the nearest message, which would put words on the clipboard that the
    /// person did not point at.
    /// </remarks>
    private Task CopyMessage()
    {
        if (App is not { } app || Transcript.Open is null)
        {
            return Task.CompletedTask;
        }

        if (Transcript.Under is not { } message || Transcript.MessageText(message) is not { } text)
        {
            Said = NoMessageHere;

            return Task.CompletedTask;
        }

        Said = Copier(app, text) ? CopiedMessage(text.Length) : NotCopied;

        return Task.CompletedTask;
    }

    /// <summary>
    /// How this surface reaches the machine's clipboard.
    /// </summary>
    /// <remarks>
    /// A seam, and it exists for exactly the reason <see cref="Opener"/> does: the suite drives
    /// this surface and a test that pressed the key would overwrite whatever the person running
    /// it had on their clipboard. That defect has been paid for once already, with a browser tab
    /// per test run, and the note on <see cref="Opener"/> is the record of it.
    /// </remarks>
    internal Func<IApplication, string, bool> Copier { get; set; } = Clipboards.Write;

    /// <summary>How often an open transcript is read again, or null not to read it again at all.</summary>
    /// <remarks>
    /// <para>
    /// <b>Asked for by the owner, who asked an agent a question, got the answer in forty-seven seconds,
    /// and saw nothing until they closed the transcript and opened it again.</b> A transcript was read
    /// once, when it was opened. The web console at least says when its page has fallen behind; this
    /// surface said nothing and showed a conversation as it had been.
    /// </para>
    /// <para>
    /// <b>Polled, rather than driven by the seat's stream.</b> The stream speaks only to a seat and only
    /// about its own mail, while a transcript is as often an operator with no seat reading two agents
    /// talk. One mechanism that works for both at a few seconds' delay is better than a fast one for half
    /// the cases and a second one for the rest. Three seconds rather than one because §16 permits a
    /// broker across a private network, and each read is an export across whatever is in between.
    /// </para>
    /// <para>
    /// <b>A seam, for the reason <see cref="Opener"/> is one.</b> The harness never pumps the main loop,
    /// and <see cref="Settled"/> is what makes a driven journey deterministic: a surface queueing a read
    /// every few seconds on its own would make assertions across the suite race it. Driving turns it
    /// off; the tests of following turn it back on.
    /// </para>
    /// </remarks>
    internal TimeSpan? FollowEvery { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How often a held seat asks what is waiting for it, or null not to ask.</summary>
    /// <remarks>
    /// <para>
    /// <b>Because the terminal told nobody.</b> Taking delivery reported what it found, and finding out
    /// meant pressing a key inside a transcript — so a person sitting in front of this surface could be
    /// owed work for an hour and see nothing, which is what the owner met an hour after 0.29.1 fixed the
    /// same fault in the console. A surface that knows who you are and will not say what is waiting is
    /// the one that wastes the most of somebody's time, because they are looking straight at it.
    /// </para>
    /// <para>
    /// <b>Asked, never accumulated.</b> The console learned this and <c>SeatHold</c> carries the scar: a
    /// notice arrives when something lands and never when something is collected, so a count kept from
    /// the stream only ever climbs and ends up contradicting the screen underneath it. This asks the
    /// broker, which is the only thing that knows.
    /// </para>
    /// <para>
    /// A seam for the reason <see cref="FollowEvery"/> is one, and separate from it: following belongs
    /// to a conversation on screen, and this belongs to a seat wherever its holder has navigated to.
    /// </para>
    /// </remarks>
    internal TimeSpan? MailEvery { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>What is waiting for the seat, and whether the broker could be asked.</summary>
    /// <remarks>
    /// Null until a seat has been taken and the first read has come back, so the status line says nothing
    /// rather than saying zero: "none waiting" and "not asked yet" are different facts, and only one of
    /// them means stop looking. The same rule the console's own count is held to.
    /// </remarks>
    internal (int Waiting, bool Certain)? Mail { get; private set; }

    /// <summary>What is said when an open transcript could not be read again.</summary>
    /// <param name="problem">What the read reported, which is a broker's words and neutralised.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    /// Said, and the transcript left as it was. Replacing what somebody is reading with an error would
    /// take the conversation off the screen because of a moment's trouble reaching the broker; saying
    /// nothing would leave it asserting it is current, which is the failure following exists to remove.
    /// </remarks>
    internal static string NotFollowing(string? problem) =>
        $"This conversation could not be read again ({Safe.Text(problem ?? "the broker did not answer")}), " +
        "so what is on screen may be out of date. It is tried again every few seconds.";

    /// <summary>Starts reading a transcript again every <see cref="FollowEvery"/>, ending any earlier follow.</summary>
    /// <param name="project">The project the conversation belongs to.</param>
    /// <param name="conversationId">The conversation now open.</param>
    private void StartFollowing(ProjectSettings project, string conversationId)
    {
        if (FollowEvery is not { } every)
        {
            return;
        }

        CancellationToken stop;

        lock (_followGate)
        {
            if (_closed)
            {
                return;
            }

            _following?.Cancel();
            _following?.Dispose();

            try
            {
                _following = CancellationTokenSource.CreateLinkedTokenSource(_reading.Token);
            }
            catch (ObjectDisposedException)
            {
                // Closing while this opened. Nothing to follow for.
                _following = null;

                return;
            }

            stop = _following.Token;
        }

        _followFailed = null;
        _ = FollowAsync(project, conversationId, every, stop);
    }

    /// <summary>Queues a read of the open transcript every interval, until it is left.</summary>
    /// <param name="project">The project.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="every">The interval.</param>
    /// <param name="stop">Ended by another conversation opening, or the surface closing.</param>
    /// <returns>When following has stopped.</returns>
    private async Task FollowAsync(
        ProjectSettings project, string conversationId, TimeSpan every, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(every, stop).ConfigureAwait(false);

                if (!StillReading(conversationId))
                {
                    return;
                }

                // One read waiting at a time. A broker slower than the interval would otherwise stack a
                // read per tick behind the first, and every action somebody took would wait on all of them.
                if (Interlocked.CompareExchange(ref _followQueued, 1, 0) == 0)
                {
                    Queue(token => FollowOnceAsync(project, conversationId, token));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Another conversation was opened, or the surface is closing. This is how it ends.
        }
    }

    /// <summary>Whether a conversation is still the one on screen.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <returns>Whether the view is a transcript and that transcript is this conversation.</returns>
    private bool StillReading(string conversationId)
    {
        Viewing viewing;

        lock (_screen)
        {
            viewing = _place.Viewing;
        }

        return viewing == Viewing.Conversation
            && string.Equals(Transcript.Open, conversationId, StringComparison.Ordinal);
    }

    /// <summary>Reads the open transcript once, and redraws it if it has changed.</summary>
    /// <param name="project">The project.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>When it has been read.</returns>
    private async Task FollowOnceAsync(
        ProjectSettings project, string conversationId, CancellationToken cancellationToken)
    {
        try
        {
            if (!StillReading(conversationId))
            {
                return;
            }

            var read = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(_directory.ClientFor(project))
                    .ExportAsync(conversationId, cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);

            if (read.Value is { } export)
            {
                // Recovered: take back the warning, but only if nothing has been said since.
                if (_followFailed is { } warned && string.Equals(Said, warned, StringComparison.Ordinal))
                {
                    Said = string.Empty;
                }

                _followFailed = null;
                _ = Transcript.Follow(conversationId, project.Name.Value, export);

                // **The room as well, every time, and whether or not the transcript changed** (2026-10-03). Somebody picking
                // a request up changes nothing in the transcript but everything in the room's marks, and "working 3m" has to
                // count. It was read only on arriving, so the room went on describing the moment the conversation opened.
                if (StillReading(conversationId))
                {
                    await FillRoomAsync(project, conversationId, export, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            // Once per outage rather than once per tick, which would bury anything else said meanwhile.
            if (_followFailed is null && StillReading(conversationId))
            {
                _followFailed = NotFollowing(read.Problem);
                Said = _followFailed;
            }
        }
        finally
        {
            _ = Interlocked.Exchange(ref _followQueued, 0);
        }
    }

    /// <summary>Asks what is waiting for the held seat, every <see cref="MailEvery"/>, until it is let go.</summary>
    /// <remarks>
    /// Started when a seat is taken and ended when it is released or the surface closes. One ask at a time,
    /// for the reason following takes one read at a time: a broker slower than the interval would otherwise
    /// stack an ask per tick in front of every action somebody took.
    /// </remarks>
    private void WatchMail()
    {
        if (MailEvery is not { } every)
        {
            return;
        }

        CancellationToken stop;

        lock (_followGate)
        {
            _watchingMail?.Cancel();
            _watchingMail?.Dispose();

            try
            {
                _watchingMail = CancellationTokenSource.CreateLinkedTokenSource(_reading.Token);
            }
            catch (ObjectDisposedException)
            {
                _watchingMail = null;

                return;
            }

            stop = _watchingMail.Token;
        }

        _ = WatchMailAsync(every, stop);
    }

    /// <summary>Queues an ask every interval while a seat is held.</summary>
    /// <param name="every">The interval.</param>
    /// <param name="stop">Ended by the seat going or the surface closing.</param>
    /// <returns>When watching has stopped.</returns>
    private async Task WatchMailAsync(TimeSpan every, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                if (Held is null)
                {
                    Mail = null;

                    return;
                }

                if (Interlocked.CompareExchange(ref _mailQueued, 1, 0) == 0)
                {
                    Queue(MailOnceAsync);
                }

                await Task.Delay(every, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The seat was let go, or the surface is closing. This is how it ends.
        }
    }

    /// <summary>Asks once what is waiting for the seat.</summary>
    /// <param name="cancellationToken">Cancels the ask.</param>
    /// <returns>When the count has been read and the screen told.</returns>
    private async Task MailOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // **Through `Voice()` rather than built here**, which `ClientSeparationTests` insists on and was
            // right to: the agents client is the one that can speak as a person, and every path to it goes
            // through the seat the book granted. A first version of this constructed its own and tripped the
            // guard -- the property held, since the seat was the held one, but a rule that has to be re-argued
            // at each call site is the rule that eventually is not.
            if (Voice() is not { } voice)
            {
                Mail = null;

                return;
            }

            var read = await BrokerDirectory.ReadAsync(async () =>
                await voice.WaitingAsync(cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);

            // Held again rather than assumed: the sweep can release a seat while this was in flight, and a
            // count left on the status line under nobody's name is the console's own released-seat fault.
            if (Held is null)
            {
                Mail = null;

                return;
            }

            Mail = read.Problem is null ? (read.Value.Messages, true) : (0, false);

            Relay();
        }
        finally
        {
            _ = Interlocked.Exchange(ref _mailQueued, 0);
        }
    }

    /// <summary>What starting every unregistered agent in this project is called.</summary>
    /// <remarks>
    /// The count is in the label because that is the last moment to say it, which is the rule the
    /// broadcast row is built on: this launches one process per agent on this machine.
    /// </remarks>
    /// <param name="howMany">How many would be started.</param>
    /// <returns>The label.</returns>
    internal static string StartEveryTitle(int howMany) =>
        $"Start the {howMany} that have never registered";

    /// <summary>Every agent here that nothing has ever registered, and this machine can start.</summary>
    /// <returns>Their names, in the order the roster shows them.</returns>
    /// <remarks>
    /// <para>
    /// <b>§14 counts a name nothing has registered as unroutable</b>, and refuses every message
    /// type to one — so these are not quiet agents, they are unaddressable ones, and no Request
    /// will wake them. What reaches them is an operator start, which is what
    /// §12.3's closing paragraph routes through the catalogue.
    /// </para>
    /// <para>
    /// <b>Which is why this exists at all.</b> 012 step 1 made enrolment leave an agent registered
    /// so that nobody has to open nineteen repositories by hand; a deployment enrolled before that,
    /// or one whose starts did not take, is left in exactly the state it removed — and the only
    /// remedies on this screen were nineteen separate keystrokes or nineteen editors.
    /// </para>
    /// <para>
    /// Retired agents are left out, as they are from a broadcast: an operator retired them, and
    /// starting them would be this screen undoing that quietly.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<AgentName> NeverRegistered() =>
    [
        .. Agents.Standings
            .Where(row => !row.Seat && !row.Retired && !row.EverRun && row.StartableHere)
            .OrderBy(row => row.Name, StringComparer.Ordinal)
            .Select(row => Resolve(row.Name, out var peer) ? (AgentName?)peer : null)
            .OfType<AgentName>(),
    ];

    /// <summary>Starts every agent here that has never registered, and says what happened.</summary>
    /// <returns>When the run is over.</returns>
    /// <remarks>
    /// Queued like every other action, so the surface stays drawn while it runs: this is one
    /// process launch per agent and a wait on each, which is the slowest thing this screen does.
    /// </remarks>
    internal Task StartEvery()
    {
        if (Current is not { } project)
        {
            return Task.CompletedTask;
        }

        var waiting = NeverRegistered();

        Queue(async cancellationToken =>
        {
            Said = await Starting.EveryAsync(
                waiting,
                (agent, token) => BrokerDirectory.ReadAsync(async () =>
                    await new BrokerClient(_directory.ClientFor(project))
                        .StartAsync(agent, "an operator started every unregistered agent", token)
                        .ConfigureAwait(false)),
                (agent, token) => RegisteredIn(project, agent, token),
                Starting.Patience,
                TimeProvider.System,

                // **Said while it happens, because this is the slowest thing on the screen.**
                // Nineteen launches and a wait is a minute of a surface that would otherwise say
                // nothing at all, and a surface that says nothing is one somebody assumes has hung.
                Report,
                cancellationToken).ConfigureAwait(false);

            await SelectAsync(project, cancellationToken).ConfigureAwait(false);
        });

        return Settled;
    }

    /// <summary>Whether a project's registry holds an agent now.</summary>
    /// <param name="project">The project to ask.</param>
    /// <param name="agent">The agent to look for.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Whether it is there, or why the project could not be asked.</returns>
    /// <remarks>
    /// <b>The registry is the only thing that knows.</b> §12.3 makes registration the adapter's own
    /// act on attach, so nothing this surface does can write one and nothing it is told by a node
    /// proves one happened. Every claim here that an agent is addressable comes through this.
    /// </remarks>
    internal Task<ProjectRead<bool>> RegisteredIn(
        ProjectSettings project, AgentName agent, CancellationToken cancellationToken) =>
        BrokerDirectory.ReadAsync(async () =>
            (await new BrokerClient(_directory.ClientFor(project))
                .RegistryAsync(cancellationToken).ConfigureAwait(false))
            .Any(row => row.Name == agent));

    /// <summary>The repository this session last added, or null when it has added none.</summary>
    /// <remarks>
    /// <b>The session, not the machine.</b> Somebody enrolling a set of repositories does them one
    /// after another out of one folder, and that is the run this shortens. Writing it down between
    /// runs would be a second place a deployment's shape is recorded, for a convenience worth one
    /// keystroke.
    /// </remarks>
    private string? _lastAdded;

    /// <summary>The folder this terminal was started from, which the add picker opens at first.</summary>
    /// <remarks>
    /// Read when the shell is built, which is when the tool starts, and kept: nothing here changes the working
    /// directory, but a folder picked up later would be whatever that was by then rather than where the person typed the
    /// command. <c>parley terminal</c> forwards without setting one, so the tool inherits the shell's. Settable so a test
    /// need not change its own process's directory, which every test running beside it shares.
    /// </remarks>
    internal string StartedIn { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>Opens the dialog that withdraws the selected agent.</summary>
    /// <returns>When the dialog is open.</returns>
    /// <remarks>
    /// <para>
    /// The description is read before it is shown, and both reads already exist: the registry for
    /// what the agent is, and the waiting graph for what it owes. Asked here rather than kept from
    /// the last draw, because a debt is exactly the fact most likely to have changed since.
    /// </para>
    /// <para>
    /// <b>The project comes from <see cref="Current"/>, and this is the sixth time that distinction
    /// has had to be made.</b> It read the projects region's selection — which a project view leaves
    /// off screen, and which a terminal opened straight into a project may never have filled at all,
    /// so the whole action returned having done nothing while the status line went on offering it.
    /// A withdrawal that silently does nothing is the one failure this dialog cannot afford, because
    /// the person believes the agent is gone.
    /// </para>
    /// <para>
    /// It also has to be the project rather than the selection now that it is passed on: a repository
    /// may answer in several, and the entry removed is the one for the project being looked at.
    /// </para>
    /// </remarks>
    internal Task Withdraw()
    {
        if (App is not { } app || Current is not { } project
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

            // **The machine's catalogue, resolved the way every other caller here resolves it.** This
            // read the project's own until 019 removed that field; passing null instead would have
            // left a withdrawn agent catalogued and startable by a peer's request.
            await Withdrawing.OpenAsync(
                app, client, row, agent, owed, project.Name.Value,
                new Catalogue(ManifestPath, CataloguePath).Proposed(), Queue,
                Reported(project))
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

    /// <summary>What showing an agent's instructions is called, before the name.</summary>
    internal const string ToldTitle = "What you have told ";

    /// <summary>The instructions dialog, while one is open.</summary>
    internal Telling? Told { get; private set; }

    /// <summary>Shows what the operator told the agent under the cursor.</summary>
    /// <returns>When the reading has finished.</returns>
    /// <remarks>
    /// <para>
    /// <b>Read-only, and this surface issues no instruction.</b> What was missing was never a second
    /// way to give one — it was any way at all to find out what became of one, because a reply
    /// reachable only through the call that asked for it survives exactly as long as that
    /// connection. Somebody at a terminal over SSH is precisely the person who cannot open the
    /// browser surface instead.
    /// </para>
    /// <para>
    /// The project comes from <see cref="Current"/> and never from the projects region's selection.
    /// That is the defect that has now appeared five times: a project view gives this region the
    /// screen, so the projects list is off screen and its selection is null.
    /// </para>
    /// </remarks>
    internal Task ShowTold()
    {
        if (App is not { } app
            || Current is not { } project
            || Agents.Selected is not { Seat: false } row
            || !AgentName.TryCreate(row.Name, out var agent))
        {
            return Task.CompletedTask;
        }

        Queue(async cancellationToken =>
        {
            var told = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(_directory.ClientFor(project))
                    .InstructionsAsync(agent, cancellationToken: cancellationToken)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            if (!told.Reachable)
            {
                Said = told.Problem!;

                return;
            }

            Told = Telling.Open(app, agent, told.Value?.Instructions ?? []).View;
        });

        return Settled;
    }

    /// <summary>The person's own agent operations, for the project on screen.</summary>
    /// <returns>The voice, or null when they hold no seat here.</returns>
    /// <remarks>
    /// Built per use rather than held, because the seat and the project can each change under it.
    /// A voice cached across a project change would speak into the wrong deployment, which is the
    /// one mistake on this surface that cannot be taken back: the transcript would record it.
    /// </remarks>
    internal SeatVoice? Voice() =>
        Held is null || Current is not { } project
            ? null
            : new SeatVoice(_directory.AgentsFor(project), Held.Seat);

    /// <summary>
    /// Said when a seat is held and no project can be resolved to speak into — a state this surface
    /// should never be in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists because the alternative was a lie.</b> Every path that speaks checks
    /// <see cref="Voice"/> and, finding nothing, said <c>Released</c> — *"Your seat was
    /// released"* — which is a statement about the seat, and the seat is right there. 0.29.1 records
    /// exactly that sentence being the defect: a console told somebody they were seated nowhere while
    /// two Results waited for the name whose seat they held.
    /// </para>
    /// <para>
    /// <b>Not reachable today, and that is the reason to say it rather than to assume it.</b>
    /// <see cref="Voice"/> read <c>Projects.Selected</c> until 020, which is null in a project view and
    /// is the shape CLAUDE.md records appearing four times. It was latent only because the one route a
    /// person has into a project view is through the list, so the list is always filled first — a
    /// guarantee resting on navigation rather than on the seat. It now reads <see cref="Current"/>,
    /// which is the view's own project; this message is what says so out loud if the state ever
    /// happens anyway, instead of blaming the seat.
    /// </para>
    /// </remarks>
    internal const string SeatWithoutProject =
        "Your seat is held, but this view resolves no project to speak into, so nothing was sent. " +
        "That should not be possible: press Escape back to the projects list and open the project " +
        "again, which rebuilds both. If it keeps happening, it is a defect worth reporting rather " +
        "than anything you did.";

    /// <summary>Takes delivery of what is waiting, after saying what that does.</summary>
    /// <returns>When the dialog has been opened.</returns>
    private Task TakeDelivery()
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

    /// <summary>Sends the Result of one of the Requests this person owes.</summary>
    /// <returns>When the dialog has been opened.</returns>
    /// <remarks>
    /// The item identifiers come from the Request as delivered. The broker checks the Result against
    /// the stored Request and names the items it expected when they do not match, so the form cannot
    /// close an exchange on items nobody asked about.
    /// </remarks>
    internal Task SendResult()
    {
        if (App is not { } app || Held is not { } hold || Voice() is not { } voice)
        {
            return Task.CompletedTask;
        }

        var owed = hold.Delivered.Where(one => one.OwesResult).ToList();

        if (owed.Count == 0)
        {
            Said = "You owe no result. Take delivery to see what is waiting.";

            return Task.CompletedTask;
        }

        var choosing = Prompting.Choose(
            Participation.ResultTitle,
            "Which request?",
            [.. owed.Select(Participation.Label)]);

        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, picked) =>
        {
            Prompting.Close(app, token);

            var chosen = owed.Find(one => Participation.Label(one) == picked);

            if (chosen is null)
            {
                Said = picked is null
                    ? string.Empty
                    : "That request is no longer one you hold.";

                return;
            }

            var exchange = chosen.Message.ExchangeId;

            // Asked for again rather than used as captured, for the reason `Compose` carries — and
            // more sharply here, because answering settles somebody else's exchange. A dialog is
            // open for as long as a person takes to choose, and the idle sweep runs on every draw.
            // `TakeDelivery` already re-checked; these two did not, which is what makes it a
            // pattern applied unevenly rather than a decision.
            Queue(cancellationToken =>
            {
                if (Voice() is not { } speaking || Held is not { } holding)
                {
                    Said = Participation.Gone;

                    return Task.CompletedTask;
                }

                return ResolveAsync(app, speaking, holding, chosen, exchange, cancellationToken);
            });
        };

        return Task.CompletedTask;
    }

    /// <summary>Sends a participant a Request, in the conversation on screen.</summary>
    /// <returns>When the dialog has been opened.</returns>
    private Task SendRequest() => Compose(Transcript.Open);

    /// <summary>Sends a participant a Request, opening a new conversation.</summary>
    /// <returns>When the dialog has been opened.</returns>
    /// <remarks>
    /// <para>
    /// <b>Offered from the conversations list, which is where somebody looks for it, and the surface's
    /// own rule said so all along</b> — actions belong to the region that shows the thing acted on,
    /// and a conversation that does not exist yet is not shown in a transcript of one that does. Both
    /// surfaces could only compose from inside an existing conversation, so a project with none had no
    /// standing place to begin: the first conversation was the hardest one to start.
    /// </para>
    /// <para>
    /// The same form either way. What differs is the name it suggests — a new one here, the open one
    /// from a transcript — which is the whole of the difference between starting and continuing, and
    /// was already what `SendRequest` did before it had anywhere sensible to be offered from.
    /// </para>
    /// </remarks>
    internal Task Start() => Compose(inConversation: null);

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        // **Every key is a touch, wherever it lands** (2026-10-03). This hooks the application's keyboard the first time
        // there is one, because a key the focused view takes never reaches `OnKeyDown`: every letter typed into the box and
        // every arrow in a list was invisible to the idle rule, so twenty minutes of writing an answer marked the person
        // away while they typed it. Found probing the owner's report of focus leaving the box.
        if (_keyboard is null && App?.Keyboard is { } keyboard)
        {
            _keyboard = keyboard;
            keyboard.KeyDown += AnyKey;
        }

        // Every draw, because there is nowhere else this could hang: the surface has no menu to
        // revisit, and 004 swept on every visit to one. What it does not do is call this a touch.
        Tick(DateTimeOffset.UtcNow);

        return base.OnDrawingContent(context);
    }

    /// <summary>Marks the person here for any key the application receives, before any view sees it.</summary>
    /// <param name="sender">The keyboard.</param>
    /// <param name="key">The key, which is left unhandled.</param>
    private void AnyKey(object? sender, Key key) => Touched();

    /// <summary>Records that somebody is actually here.</summary>
    /// <param name="at">When, or now when a caller has no reason to say.</param>
    /// <remarks>
    /// A key, a click, or an action taken. Drawing is not on the list, which is the whole point:
    /// an abandoned terminal goes on drawing, and a touch taken from a draw would keep its seat
    /// alive for ever. The instant is a parameter for the same reason <see cref="Tick"/>'s is —
    /// a test moves twenty minutes rather than waiting them.
    /// <para>
    /// <b>It is also what brings the person back</b> (specs/023). An idle drop lets the broker read them as away; the
    /// first key afterwards takes the stream again, silently, because nothing was taken from them. Only one retake is
    /// queued however many keys arrive while it runs.
    /// </para>
    /// </remarks>
    internal void Touched(DateTimeOffset? at = null)
    {
        _touched = at ?? DateTimeOffset.UtcNow;

        if (Seats is null || Held is not null || Current is not { } project
            || Interlocked.CompareExchange(ref _retaking, 1, 0) != 0)
        {
            return;
        }

        Queue(async token =>
        {
            try
            {
                await EnsureSeatAsync(project, null, token).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _retaking, 0);
            }
        });
    }

    /// <inheritdoc/>
    protected override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Taken here rather than left to the toolkit's own navigation, which walks every focusable
        // view including the list inside a region. What a person means by Tab on this surface is
        // "the next region", and a Tab that stepped into a list and then out of it again would
        // visit eight places to reach four.
        Touched();

        // **Tab completes an open list, and otherwise means what it means everywhere else on this surface.**
        // The owner asked for Tab by name — "hitting tab will complete the selected item from the list" — and
        // the one thing that makes it unambiguous is that the list is on screen while it applies. With no list
        // showing there is nothing to complete and Tab is the next region, which is what it is in every pane.
        if (key == Key.Tab && Line.Editing && Line.Offering.Count > 0)
        {
            Complete();

            return true;
        }

        if (key == Key.Tab)
        {
            Move(forwards: true);

            return true;
        }

        // Up and down move the list's selection while the box keeps focus (FR-032), which is what keeps Enter
        // answerable: what Enter does is decided by what is active, and the list is never active.
        if ((key == Key.CursorUp || key == Key.CursorDown) && Line.Editing && Line.Offering.Count > 0)
        {
            _listing = _listing.Move(key == Key.CursorDown ? 1 : -1);
            Line.Offer(_listing);

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

        // **Enter from the box is not taken here, and finding out why cost the owner a morning.** A focused
        // view is offered a key before this handler is, so what reaches here is what that view declined: Tab
        // arrives because the box does not bind it, and Enter does not arrive at all because the box does.
        // An arm here for Enter is an arm that never runs. `LineRegion` takes it on the box itself.
        //
        // **Enter in a folded transcript opens the message under the cursor**, which is the primary action of
        // the active control read at the level of a fold: a folded header is a message somebody is asking to
        // see. Unfolded it does nothing and is left alone, rather than swallowed to no effect.
        if (key == Key.Enter
            && Panes[FocusedRegion] is TranscriptRegion
            && Transcript.Under is { } message
            && Transcript.Expand(message))
        {
            return true;
        }


        // Enter on a setting changes it, which is the one thing a row in that list is for.
        if (key == Key.Enter && Panes[FocusedRegion] is ConfigurationRegion && Configuration.Selected is not null)
        {
            _ = ChangeSetting();

            return true;
        }

        // **Enter on an agents-list row opens what can be done with it** (specs/023 User Story 6). It took a seat until
        // then, which went with the typed name. The agents list raises no `Accepting` of its own, so nothing is preempted.
        if (key == Key.Enter && Panes[FocusedRegion] is AgentsRegion && Agents.Selected is not null)
        {
            _ = RowMenu();

            return true;
        }

        // **A box somebody types into wins every printable key.** Since 2026-10-01 no action is a bare character, so
        // this is the second line rather than the only one — and the owner's report is why: a `c` typed into a message
        // copied the conversation, because this guard holds only while the field itself has focus, and focus had moved
        // off the field and stayed in its pane. What follows is the history of the first line.
        //
        // Every action on this surface was a letter, and this handler takes a region's letter before the focused
        // view sees it — so without this, typing a question mark into an ask opened the actions menu and typing
        // `x` cleared the message line. Nobody had met it because the box was not on screen: the three-pane
        // arrangement was never written, so the line region had no size and drew nothing, while `LineTests`
        // set its text in memory and passed. **A property the suite holds to and the screen never shows**, which
        // is the third time this repository has recorded that shape — `Shell.Said` shown to nobody, and a
        // terminal with no colour under a passing `ColourTests`.
        //
        // `Lists` records the same collision from the other side: a list's own type-to-find was eating every
        // letter the status line advertised. One of the two has to yield, and while a text box has focus the
        // answer is obvious.
        //
        // Below the keys above rather than above them: Tab, Escape, the quit chord and F8 keep working
        // while somebody types, because none of them is a character and leaving a box must not need the mouse.
        if (Line.Editing && !key.IsCtrl && !key.IsAlt && key.AsRune.Value != 0)
        {
            return base.OnKeyDown(key);
        }

        // The letter shown beside an action in the status line takes it, when its region has focus.
        // Every one of them is also a menu entry or a button the mouse reaches: contracts/surface.md
        // is explicit that no action exists only as a key.
        if (Typed(key, ActionsKey))
        {
            _ = Offer();

            return true;
        }

        // **F10 opens the bar, and Alt and a letter is an action** (the owner's call of 2026-10-01). Alt and a letter
        // opened a menu until then, while every action was a bare letter — so a letter typed anywhere the box did not
        // hold focus set one off. Actions took Alt so that a plain character is never one; the menus moved to F10, the
        // key most programs with a bar use, and Left and Right walk from the first menu to the rest.
        if (key == MenuKey)
        {
            _ = _bar.OpenFirst();

            return true;
        }

        foreach (var action in Actions())
        {
            if (action.Answers(key))
            {
                _ = action.Run();

                return true;
            }
        }

        // **After the region's, so a pane's own letter always wins.** These two are on every
        // screen, and a global key that shadowed a region's would take an action away from
        // wherever it was added later — silently, and only on the screens that had it.
        foreach (var action in Messaging())
        {
            if (action.Answers(key))
            {
                _ = action.Run();

                return true;
            }
        }

        // **Last of the three, and dispatched here because otherwise it would be offered and not
        // taken.** `Global()` reaches the menu bar, so its keys are named on screen — and a key this
        // surface names and then ignores is the inverse of the defect the rule above guards, and the
        // worse half: an action nothing offers can still be found, while a key that does nothing
        // teaches somebody the surface is broken.
        foreach (var action in Global())
        {
            if (action.Answers(key))
            {
                _ = action.Run();

                return true;
            }
        }

        return base.OnKeyDown(key);
    }

    /// <summary>How an action's character is written, wherever a person is told the key.</summary>
    /// <param name="character">The character.</param>
    /// <returns><c>Alt+</c> and the character.</returns>
    /// <remarks>One spelling, so the status line, the menus and every sentence that names a key cannot disagree.</remarks>
    internal static string Alt(char character) => "Alt+" + character;

    /// <summary>The key that opens the menu bar.</summary>
    internal static Key MenuKey => Key.F10;

    /// <summary>Whether a keystroke is Alt and the character an action is bound to.</summary>
    /// <param name="key">What arrived.</param>
    /// <param name="character">The character an action is bound to.</param>
    /// <returns><see langword="true"/> when the two are the same keystroke.</returns>
    /// <remarks>
    /// <para>
    /// <b>Alt, always, on the owner's call of 2026-10-01.</b> Every action here was a bare letter, so a letter typed
    /// anywhere the box did not happen to hold focus was an action: the owner typed a <c>c</c> into a message and the
    /// conversation went to the clipboard. The box's own guard could only ever cover the moment it had focus. With
    /// a modifier a plain character is never an action, wherever focus is, so no guard has to be right for typing to
    /// be safe. The menus moved to F10 to free Alt.
    /// </para>
    /// <para>
    /// <b>The character, not the key code.</b> <c>?</c> is Shift and another key on every keyboard
    /// there is, and a driver is free to say so by carrying the shift modifier on what it hands
    /// up. Comparing against a bare key code then matches under one driver and not another — the
    /// same keystroke, the same code, working on the machine the suite runs on and dead on
    /// somebody's terminal. That is what happened, and it is invisible from inside a test that
    /// injects the key it is about to compare against. <b>The character is read with Alt taken off</b>, because
    /// <c>Key.AsRune</c> is empty for any key carrying a modifier — 0.34.2 records the bar advertising keys that did
    /// nothing for exactly that reason. A letter matches in either case, so Caps Lock does not disable every action.
    /// </para>
    /// <para>
    /// Control is excluded rather than ignored. Ctrl and Alt together is how a terminal spells a different keystroke,
    /// and on several keyboard layouts it is AltGr, which types an ordinary character.
    /// </para>
    /// </remarks>
    internal static bool Typed(Key key, char character) =>
        key.IsAlt
        && !key.IsCtrl
        && key.NoAlt.AsRune is { Value: > 0 } typed
        && (typed == new Rune(character)
            || (char.IsAsciiLetter(character) && Rune.ToLowerInvariant(typed) == new Rune(char.ToLowerInvariant(character))));

    /// <summary>Reads the manifest and every broker, and draws the projects.</summary>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>When the projects region has been drawn.</returns>
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var projects = _directory.Projects;
        var (health, figures) = await SurveyAsync(projects, cancellationToken).ConfigureAwait(false);

        Projects.Show(projects, health, _directory.ManifestProblem, figures);

        if (Projects.Selected is { } selected)
        {
            await SelectAsync(selected, cancellationToken).ConfigureAwait(false);
        }

        // **In every project, not only the one selected** (FR-014): the person is here in every project this terminal
        // shows, so an agent in any of them reads them as here. The selected one was taken just now with its registry.
        foreach (var project in projects.Where(p => Projects.Selected is not { } s || s.Name != p.Name))
        {
            await EnsureSeatAsync(project, null, cancellationToken).ConfigureAwait(false);
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

        Agents.Show(registry.Value, Catalogue(), registry.Problem);
        Conversations.Show(
            Ordered(conversations.Value?.Items)?.Where(c => ConversationsOf is not { } only || c.Participants.Contains(only))
                .ToList(),
            conversations.Problem,
            _archivedOmitted,
            _showArchived,
            ConversationsOf);
        Transcript.Clear();

        // The person's seat, from the registry just read (specs/023). No name is asked for: the broker provides it.
        await EnsureSeatAsync(project, registry.Value, cancellationToken).ConfigureAwait(false);
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

    /// <summary>What showing a project's health is called.</summary>
    private const string HealthTitle = "What needs you, and what the work came to";

    /// <summary>Shows the project's flags, waits, results, refusals and clarifications.</summary>
    /// <returns>When the reading has finished.</returns>
    /// <remarks>
    /// The project on screen, or the one under the cursor in the projects list, since this is offered from both.
    /// </remarks>
    private Task ShowHealth()
    {
        if (App is not { } app || (Current ?? Projects.Selected) is not { } project)
        {
            return Task.CompletedTask;
        }

        Queue(async cancellationToken =>
        {
            var client = new BrokerClient(_directory.ClientFor(project));

            var read = await BrokerDirectory.ReadAsync(async () => Health.Lines(
                await client.FlagsAsync(cancellationToken).ConfigureAwait(false),
                await client.WaitingAsync(cancellationToken).ConfigureAwait(false),
                await client.ResultsAsync(cancellationToken).ConfigureAwait(false),
                await client.RefusalsAsync(cancellationToken).ConfigureAwait(false),
                await client.ClarificationsAsync(cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);

            if (!read.Reachable)
            {
                Said = read.Problem!;

                return;
            }

            _ = Health.Open(app, project.Name.Value, read.Value!);
        });

        return Settled;
    }

    /// <summary>Opens the dialogs that close the selected conversation now.</summary>
    /// <returns>When the first dialog is open.</returns>
    private Task CloseSelected()
    {
        if (App is not { } app
            || Conversations.Selected is not { } conversation
            || Current is not { } project)
        {
            return Task.CompletedTask;
        }

        var row = ConversationsView.Rows([conversation])[0];
        var client = new BrokerClient(_directory.ClientFor(project));

        return Closing.OpenAsync(app, client, row, Queue, async said =>
        {
            Said = said;

            await SelectAsync(project, _reading.Token).ConfigureAwait(false);
        });
    }

    /// <summary>Opens the form that instructs the agent under the cursor.</summary>
    /// <returns>When the form is open.</returns>
    private Task InstructSelected()
    {
        if (App is not { } app
            || Current is not { } project
            || Agents.Selected is not { Seat: false } row
            || !AgentName.TryCreate(row.Name, out var agent))
        {
            return Task.CompletedTask;
        }

        return Instructing.OpenAsync(
            app, new BrokerClient(_directory.ClientFor(project)), agent, Queue, said =>
            {
                Said = said;

                return Task.CompletedTask;
            });
    }

    /// <summary>Opens the form that records what the agent under the cursor owns.</summary>
    /// <returns>When the form is open.</returns>
    /// <remarks>
    /// Guarded the same way <see cref="InstructSelected"/> is, including the seat exclusion: a seat
    /// is a person, and an ownership line is how peers choose which <em>agent</em> to send work to.
    /// </remarks>
    private Task OwnershipOfSelected()
    {
        if (App is not { } app
            || Current is not { } project
            || Agents.Selected is not { Seat: false } row
            || !AgentName.TryCreate(row.Name, out var agent))
        {
            return Task.CompletedTask;
        }

        return Ownership.OpenAsync(
            app,
            new BrokerClient(_directory.ClientFor(project)),
            agent,
            row.Workspace,
            project.Name.Value,
            Queue,
            said =>
            {
                Said = said;

                return Task.CompletedTask;
            });
    }

    /// <summary>Asks every registered agent in this project what it owns.</summary>
    /// <returns>When the dialog is open.</returns>
    /// <remarks>
    /// <b>The seats are excluded, because a seat is a person.</b> An ownership line is how peers decide
    /// which agent to send work to, and somebody holding a seat is not routed to by a roster entry —
    /// asking them would spend a turn on a question that cannot apply. Retired agents go too: §10.2's
    /// retire stops them being routed to at all, so what they own is no longer a question anybody acts on.
    /// </remarks>
    private Task AskEveryoneWhatTheyOwn()
    {
        if (App is not { } app || Current is not { } project)
        {
            return Task.CompletedTask;
        }

        // Parley is excluded as well as the seats: it asks, it is never asked.
        var asking = Agents.Registry
            .Where(record => record.Status != AgentStatus.Retired)
            .Where(record => !string.Equals(record.Adapter, Seat.Adapter, StringComparison.Ordinal))
            .Where(record => !record.Facilitator)
            .Select(record => record.Name)
            .ToList();

        return Ownership.AskEveryAsync(
            app, new BrokerClient(_directory.ClientFor(project)), asking, Queue, said =>
            {
                Said = said;

                return Task.CompletedTask;
            });
    }

    /// <summary>Opens the ownership review for this project.</summary>
    /// <returns>When the review has been queued.</returns>
    /// <remarks>
    /// Handed the registry this surface last read, which is what the rows are built from; the question each agent is on
    /// is read afresh inside, because that is the half that moves while a person is looking.
    /// </remarks>
    private Task ReviewOwnership()
    {
        if (App is not { } app || Current is not { } project)
        {
            return Task.CompletedTask;
        }

        return OwnershipReview.OpenAsync(
            app, new BrokerClient(_directory.ClientFor(project)), Agents.Registry, project.Name.Value, Queue, said =>
            {
                Said = said;

                return Task.CompletedTask;
            });
    }

    /// <summary>Asks the agent under the cursor what it owns.</summary>
    /// <returns>When the dialog is open.</returns>
    /// <remarks>
    /// The seat is read here and passed in, rather than looked up inside the dialog, for the reason
    /// <see cref="Voice"/> exists at all: whether somebody can speak is a fact about this view and
    /// its seat, and a screen that asked a different region would be the defect this file records
    /// five times.
    /// </remarks>
    private Task AskWhatItOwns()
    {
        if (App is not { } app
            || Current is not { } project
            || Agents.Selected is not { Seat: false } row
            || !AgentName.TryCreate(row.Name, out var agent))
        {
            return Task.CompletedTask;
        }

        return Ownership.AskAsync(
            app, new BrokerClient(_directory.ClientFor(project)), agent, Queue, said =>
            {
                Said = said;

                return Task.CompletedTask;
            });
    }

    /// <summary>Offers what an agent proposed in the open discussion for acceptance.</summary>
    /// <returns>When the discussion has been read and the dialog is open.</returns>
    /// <remarks>
    /// The roster is handed over as a lookup rather than a row, because which agent this is about is
    /// decided by the discussion and not by the cursor — so what the registry says about it cannot
    /// be gathered until afterwards.
    /// </remarks>
    private Task AcceptProposed()
    {
        if (App is not { } app
            || Current is not { } project
            || Transcript.Open is not { } conversation)
        {
            return Task.CompletedTask;
        }

        var client = new BrokerClient(_directory.ClientFor(project));

        return Ownership.ReviewAsync(
            app,
            client,
            conversation,
            Agents.Registry,
            project.Name.Value,
            Queue,
            said =>
            {
                Said = said;

                return Task.CompletedTask;
            },
            _reading.Token);
    }

    /// <summary>What the roster says about one agent.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>Its workspace and the line peers read today, either of which may be absent.</returns>
    /// <remarks>
    /// <para>
    /// Matched on the whole name rather than the role, because two machines running one role is an
    /// ordinary deployment and the workspace returned is what decides which file gets written —
    /// 005's gate found a withdrawal deleting a different agent's launch configuration by being
    /// looser than this.
    /// </para>
    /// <para>
    /// <b>Read from the registry record and not from the drawn row, which is a defect this method
    /// shipped with for one commit.</b> A row's ownership cell is a sentence for a person to read and
    /// says <c>not stated</c> where an agent has said nothing — so asking the row put those two words
    /// in front of somebody as the line their peers were reading, and would have written them into a
    /// launch configuration had they been accepted.
    /// </para>
    /// </remarks>
    private (string? Workspace, string? Live) Registered(AgentName agent)
    {
        var record = Agents.Registry.FirstOrDefault(r => r.Name == agent);

        return record is null ? (null, null) : (record.Workspace, record.Owns);
    }

    /// <summary>Lists the selected project's files, and what can be done with each.</summary>
    /// <returns>When the list has been read and opened.</returns>
    private Task ShowFiles() => ShowFilesIn(null);

    /// <summary>Lists the selected conversation's files, and what can be done with each.</summary>
    /// <returns>When the list has been read and opened.</returns>
    private Task ShowConversationFiles() =>
        Conversations.Selected is { } chosen ? ShowFilesIn(chosen.Id) : Task.CompletedTask;

    private Task ShowFilesIn(string? conversation)
    {
        if (App is not { } app || (Current ?? Projects.Selected) is not { } project)
        {
            return Task.CompletedTask;
        }

        _ = Files.OpenAsync(
            app, new BrokerClient(_directory.ClientFor(project)), project, conversation, FileLauncher, Queue, said =>
            {
                Said = said;

                return Task.CompletedTask;
            });

        return Settled;
    }

    /// <summary>Opens the Auto Accept switch for the project in view, and reads the settings again afterwards.</summary>
    /// <returns>When the read that opens the dialog has been queued.</returns>
    /// <remarks>
    /// Read again whatever was chosen, so the row always says what the broker holds rather than what this surface
    /// believes it asked for.
    /// </remarks>
    private Task AutoAccept()
    {
        if (App is not { } app || Current is not { } project)
        {
            return Task.CompletedTask;
        }

        return AutoAccepting.OpenAsync(
            app, new BrokerClient(_directory.ClientFor(project)), project, Queue, async said =>
            {
                Said = said;

                if (_place.Viewing == Viewing.Configuration)
                {
                    await ReadConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                }
            });
    }

    private Task PruneSelected()
    {
        if (App is not { } app || (Current ?? Projects.Selected) is not { } project)
        {
            return Task.CompletedTask;
        }

        return Pruning.OpenAsync(
            app, new BrokerClient(_directory.ClientFor(project)), project, Queue, said =>
            {
                Said = said;

                return Task.CompletedTask;
            });
    }

    /// <summary>Sends a clarification on something this person holds.</summary>
    /// <returns>When the dialog has been opened.</returns>
    /// <remarks>
    /// Offered on anything owed — a Request, to ask its requester for what only it can give, or a Clarify, to
    /// answer it. The seat is asked for again at the send, for the reason <see cref="SendResult"/> gives.
    /// </remarks>
    private Task Clarify()
    {
        if (App is not { } app || Held is not { } hold || Voice() is null)
        {
            return Task.CompletedTask;
        }

        var held = hold.Delivered.Where(one => one.Owes).ToList();

        if (held.Count == 0)
        {
            Said = "You hold nothing to clarify. Take delivery to see what is waiting.";

            return Task.CompletedTask;
        }

        var choosing = Prompting.Choose(Participation.ClarifyTitle, "Which one?", [.. held.Select(Participation.Label)]);
        var picking = Prompting.Open(app, choosing);

        choosing.Done += (_, picked) =>
        {
            Prompting.Close(app, picking);

            var chosen = held.Find(one => Participation.Label(one) == picked);

            if (chosen is null)
            {
                Said = picked is null ? string.Empty : "That is no longer one you hold.";

                return;
            }

            var form = new FormDialog(
                Participation.ClarifyTitle + Environment.NewLine + Participation.Asked(chosen),
                [new FormField("What do you need from them, or what did they ask for?", string.Empty, Participation.RequiredProblem, Long: true)]);

            var writing = Prompting.Open(app, form);

            form.Done += (_, values) =>
            {
                Prompting.Close(app, writing);

                if (values is null)
                {
                    Said = string.Empty;

                    return;
                }

                var text = values[0];

                Queue(async cancellationToken =>
                {
                    if (Voice() is not { } speaking || Held is not { } holding)
                    {
                        Said = Participation.Gone;

                        return;
                    }

                    var sent = await speaking
                        .ClarifyAsync(holding, chosen.Message.ExchangeId, text, cancellationToken)
                        .ConfigureAwait(false);

                    Said = sent.Says;

                    if (Current is { } project)
                    {
                        await SelectAsync(project, cancellationToken).ConfigureAwait(false);
                    }
                });
            };
        };

        return Task.CompletedTask;
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

        await FillRoomAsync(project, conversationId, read.Value, cancellationToken).ConfigureAwait(false);

        // Followed only once it has been read. A conversation that could not be read already says why,
        // and asking a broker that did not answer every few seconds would say it again each time.
        if (read.Problem is null)
        {
            StartFollowing(project, conversationId);
        }
    }

    /// <summary>Fills the room's column from the conversation and the broker's wait-for graph.</summary>
    /// <param name="project">The project.</param>
    /// <param name="conversationId">Which conversation, so a draft is never carried into another one.</param>
    /// <param name="export">The conversation, or null when it could not be read.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>When the column has been drawn.</returns>
    /// <remarks>
    /// <para>
    /// <b>The wait-for graph is the second read, and it is what makes one input line possible.</b> An exchange
    /// snapshot carries no parties, so whose move it is comes from the broker's own graph rather than from
    /// reconstructing turn logic here (`research.md` R1).
    /// </para>
    /// <para>
    /// A graph that could not be read leaves the participants listed and nothing owed, rather than an empty
    /// column: who is in a conversation is known from the export alone, and losing that as well would report
    /// less than was actually read.
    /// </para>
    /// </remarks>
    private async Task FillRoomAsync(
        ProjectSettings project,
        string conversationId,
        ConversationExport? export,
        CancellationToken cancellationToken)
    {
        if (export is null)
        {
            Room.Show([], [], Held is not null);

            return;
        }

        if (Held is not { } hold)
        {
            // No seat, so there is no "you" for anything to be owed by. The participants are still worth
            // showing, and the column says a seat is wanted.
            Room.Show(
                [.. Pairing.Participants(export).OrderBy(a => a.Value, StringComparer.Ordinal)
                    .Select(a => new PairState(a, null, Turn.Nobody))],
                [],
                seated: false);

            return;
        }

        var waiting = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(_directory.ClientFor(project))
                .WaitingAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        // **Who is running, for the marks beside each name** (2026-10-03). A registry that could not be read leaves the
        // marks blank rather than guessing, which `Activities` says and `RoomRegion.Glyph` draws.
        var registry = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(_directory.ClientFor(project))
                .RegistryAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        var activity = Activities.Of(export, waiting.Value ?? [], registry.Value);

        var pairs = Pairing.From(export, waiting.Value ?? [], hold.Seat.Name);

        // Rebuilt from the pair state that was just read, keeping what the person had already decided about it: the
        // chips a completion wrote and who the line is addressed to — **for this conversation only** (`_drafted`).
        var here = (project.Name.Value, conversationId);
        var was = _drafted == here ? Drafting : null;

        // Parley is never a recipient: it receives nobody's work, and a line addressed to it would be refused by the
        // broker (SPEC.md §10.1). It stays in the room column, named, because it is in the conversation.
        Drafting = new Draft([.. pairs.Where(pair => !Facilitation.Is(pair.Peer))]) { Text = Line.Written };
        _drafted = here;
        _draftedWith = hold;

        if (was is null && Addressing.Recall(here.Item1, here.Item2) is { } remembered)
        {
            // **What the person chose last time they were here, restarts included** (`AddressBook`). Only agents still in
            // the conversation: addressing one that is not would open a request and pull it in, which is a different act
            // from the one that was remembered. Nobody left means everyone, which is where a conversation starts.
            var still = remembered.Where(agent => Drafting.Pairs.Any(pair => pair.Peer == agent)).ToList();

            if (still.Count > 0)
            {
                Drafting.AddressTo(still);
            }
        }

        if (was is not null)
        {
            // **The chips, or the words stop being words.** `Text` is carried above, so a name a
            // completion wrote is still in the box — while the record that it was *addressing* lived only on
            // the draft being replaced. Losing it means `Says` has nothing to take back out, and the next
            // Enter puts `@name` into a payload: the one thing §13's envelope exists to have ignored, with
            // nothing on screen saying so. Found by 017's review gate, lens 1, driven by re-opening the
            // conversation with a completion already in the box.
            foreach (var chip in was.Chips)
            {
                Drafting.Chip(chip);
            }

            // **And the addressing, which is the person's own decision and not the deployment's.** A draft
            // narrowed to one agent would otherwise widen itself back to the room while somebody was still
            // typing to them.
            if (was.Narrowed)
            {
                Drafting.AddressTo([.. was.Recipients]);
            }
            else
            {
                foreach (var added in was.Recipients.Except(pairs.Select(pair => pair.Peer)))
                {
                    Drafting.Widen(added);
                }
            }
        }

        // **The column is drawn after the draft, because its ticks are the draft's recipients.** Drawing it
        // first would show the set from the previous read until the next one finished, which is the whole of
        // what a checklist must never do.
        Room.Show(pairs, Outstanding(pairs, export), seated: true, hold.Seat.Name, Drafting.Recipients, activity);
        Line.Show(Drafting, seated: true, Near());
    }

    /// <summary>What this person must address in the conversation on screen.</summary>
    /// <param name="pairs">The pair state for every participant.</param>
    /// <param name="export">The conversation as it was read, for the message each row points at.</param>
    /// <returns>One item per thing owed, in the order the participants are listed.</returns>
    /// <remarks>
    /// Read from the same pair state the input line derives its acts from, so the column and the line cannot
    /// disagree about what is outstanding. Two sources for one fact is how a surface comes to promise something
    /// it will not do.
    /// </remarks>
    private static IReadOnlyList<OutstandingItem> Outstanding(
        IReadOnlyList<PairState> pairs,
        ConversationExport export) =>
        [.. pairs
            .Where(pair => pair.Turn == Turn.Mine && pair.OpenExchange is not null)
            .Select(pair => new OutstandingItem(
                pair.Peer,
                $"{pair.Peer.Value} — waiting for your reply",
                pair.OpenExchange!.Value,
                Asked(export, pair)))];

    /// <summary>The message that put this person on the hook, so the column can point at it.</summary>
    /// <param name="export">The conversation as it was read.</param>
    /// <param name="pair">The pair state.</param>
    /// <returns>The message identifier, or null when the export does not carry one.</returns>
    /// <remarks>
    /// <b>The latest thing the peer said in that exchange, not the first.</b> An exchange can hold a Request
    /// and then a Clarify on it, and what is actually owed an answer is the last question asked -- which is
    /// also what somebody scrolling to it wants to read. Null rather than a guess where nothing matches: a
    /// row that cannot point anywhere says so by doing nothing, which beats moving a reader somewhere
    /// arbitrary.
    /// </remarks>
    private static long? Asked(ConversationExport export, PairState pair) =>
        export.Messages
            .Where(message => message.ExchangeId == pair.OpenExchange && message.From == pair.Peer)
            .Select(message => (long?)message.Id)
            .LastOrDefault();

    /// <summary>
    /// Reads this machine's catalogue, which is the one fact about a deployment the broker never
    /// sees.
    /// </summary>
    /// <returns>What this machine could start, or an empty catalogue.</returns>
    /// <remarks>
    /// <para>
    /// Read fresh on every draw rather than held. Somebody may edit it by hand while this is open,
    /// and an agents region showing a catalogue from a minute ago would report an agent that is no
    /// longer startable as though it were.
    /// </para>
    /// <para>
    /// <b>It took a project and read that project's file</b> (018). One checkout may answer in
    /// several, so the same repository showed as startable in the project whose file happened to
    /// name it and unstartable everywhere else — which is the defect, drawn on screen. It is one
    /// file per machine now, and the same one the write goes to.
    /// </para>
    /// </remarks>
    private NodeCatalogue Catalogue() =>
        NodeCatalogue.TryReadFrom(new Catalogue(ManifestPath, CataloguePath).Proposed(),
            out var catalogue, out _)
            ? catalogue
            : NodeCatalogue.Empty;

    /// <summary>Asks every project's broker what it speaks and what it holds, all at once.</summary>
    /// <param name="projects">The projects to ask.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>What each said, and what is in each that answered, by project name.</returns>
    /// <remarks>
    /// The counts are asked only of a broker that answered in this build's protocol. One that did not
    /// could not answer the next two questions either, and asking would add two more timeouts to every
    /// refresh of a deployment that already has something wrong with it. A count that could not be read
    /// is left out rather than shown as zero.
    /// </remarks>
    private async Task<(IReadOnlyDictionary<string, ProjectHealth> Health, IReadOnlyDictionary<string, ProjectFigures> Figures)>
        SurveyAsync(IReadOnlyList<ProjectSettings> projects, CancellationToken cancellationToken)
    {
        var asked = projects.Select(async project =>
        {
            var client = new BrokerClient(_directory.ClientFor(project));

            var read = await BrokerDirectory.ReadAsync(async () =>
                await client.ImplementsAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

            var health = new ProjectHealth(read.Value, read.Problem);

            if (!health.Agrees)
            {
                return (project.Name.Value, Health: health, Figures: (ProjectFigures?)null);
            }

            var registry = await BrokerDirectory.ReadAsync(async () =>
                await client.RegistryAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

            var conversations = await BrokerDirectory.ReadAsync(async () =>
                await client.ConversationsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);

            var figures = registry.Value is { } agents && conversations.Value is { } page
                ? ProjectFigures.Of(agents, page)
                : null;

            return (project.Name.Value, Health: health, Figures: figures);
        });

        var answers = await Task.WhenAll(asked).ConfigureAwait(false);

        return (
            answers.ToDictionary(a => a.Value, a => a.Health, StringComparer.Ordinal),
            answers
                .Where(a => a.Figures is not null)
                .ToDictionary(a => a.Value, a => a.Figures!, StringComparer.Ordinal));
    }

    /// <summary>Collects an outcome per item, then sends them together as the Result.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="voice">The person's own participant operations.</param>
    /// <param name="hold">The seat holding the Request.</param>
    /// <param name="owed">The delivered Request.</param>
    /// <param name="exchange">The exchange it belongs to.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>When the Result has been sent, or abandoned.</returns>
    /// <remarks>
    /// One form for the whole exchange rather than a dialog per item: a person reporting on the third cannot
    /// otherwise revise the first. The outcome for each is chosen from <c>SeatVoice.Outcomes</c>, so
    /// <c>closed</c> can never appear among them.
    /// </remarks>
    private Task ResolveAsync(
        IApplication app,
        SeatVoice voice,
        SeatHold hold,
        Delivered owed,
        long exchange,
        CancellationToken cancellationToken)
    {
        var ids = owed.Items();

        if (ids.Count == 0)
        {
            Said = "That request carried no items this can report on.";

            return Task.CompletedTask;
        }

        var fields = new List<FormField>();

        foreach (var id in ids)
        {
            fields.Add(new FormField(Participation.OutcomeLabel(id), SeatVoice.Outcomes[0].Value, Outcome));
            fields.Add(new FormField(Participation.SaidLabel(id), string.Empty, Participation.ResultProblem));
        }

        var form = new FormDialog(
            Participation.ResultTitle + Environment.NewLine + Participation.Asked(owed),
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
                // **Asked for again here too, which is the third place and the one that was easiest
                // to miss.** The outer dialog picks a question and this one takes the words, so the
                // seat can go while somebody writes the answer — after the check that let them
                // start. Re-checking only where the dialog opened would have left exactly this gap,
                // which is why the rule is "at the send" rather than "at the composer".
                if (Voice() is not { } speaking || Held is not { } holding)
                {
                    Said = Participation.Gone;

                    return;
                }

                // Through the overload that settles the seat as well, so this surface never strikes
                // a Request off its own list while the broker still believes it open.
                var answered = await speaking
                    .ResultAsync(holding, exchange, resolutions, token2)
                    .ConfigureAwait(false);

                Said = answered.Says;

                if (Projects.Selected is { } project)
                {
                    await SelectAsync(project, token2).ConfigureAwait(false);
                }
            });
        };

        return Task.CompletedTask;

        static string? Outcome(string typed) =>
            SeatVoice.Outcomes.Any(o => string.Equals(o.Value, typed.Trim(), StringComparison.Ordinal))
                ? null
                : $"One of: {string.Join(", ", SeatVoice.Outcomes.Select(o => o.Value))}";
    }

    /// <summary>Opens the form that sends a participant a Request.</summary>
    /// <param name="inConversation">
    /// The conversation to continue, or null to suggest a new one. **This is the whole difference between
    /// starting and continuing.**
    /// </param>
    /// <param name="to">
    /// The agent the Request is for, when the person already chose it on its row, or null to ask. Asking again for
    /// an agent somebody has just picked is the picker contradicting the row (specs/023 FR-018).
    /// </param>
    /// <returns>When the dialog has been opened.</returns>
    /// <remarks>
    /// <para>
    /// One composer, because there is one way to say something to an agent and it is answered (principle
    /// 2.7). An announcement is a Request asking for an acknowledgement.
    /// </para>
    /// <para>
    /// The fields are collected in the order §5.1 puts them — why, then what, then what counts as finished.
    /// </para>
    /// </remarks>
    private Task Compose(string? inConversation, AgentName? to = null)
    {
        if (App is not { } app || Voice() is not { } voice || Projects.Selected is not { } project)
        {
            return Task.CompletedTask;
        }

        // **Already chosen, from the row it was started on** (specs/023 User Story 6): Enter on an agent's row offers to
        // start a conversation with that agent, and asking which peer again would be asking a question just answered.
        if (to is { } chosenAlready)
        {
            Open(chosenAlready, null);

            return Task.CompletedTask;
        }

        // **The peer is chosen from the roster rather than typed, and that is what settles the
        // naming question rather than working around it.** A row is spelled by whichever source
        // knows it — a registration is `role@machine`, a catalogue key is a role — and asking
        // somebody to retype either was how the composer came to refuse the name the list in front
        // of them had just printed. Choosing carries the identity; the label only has to read well.
        //
        // Everything the project has, minus this person's own seat: nobody asks themselves, and an
        // agent that has never run is a legitimate thing to ask — §12.3 routes spawn-on-demand
        // through the catalogue, which is exactly what a never-run-but-startable row is.
        var peers = Peers();

        if (peers.Count == 0)
        {
            Said = Participation.NoPeers;

            return Task.CompletedTask;
        }

        // Its own title, not the composer's. `ActionShapeTests` requires every dialog to answer to a
        // distinct one -- two sharing it makes every `Find` in the suite ambiguous, and it drove the
        // picker where it meant the form.
        // **Everybody at once, on both paths.** It was offered only when announcing, on a reason
        // that turned out to be wrong — §7.1's cap is per peer, so N questions to N different peers
        // hold one slot each and saturate nothing. `Participation.Everyone` carries what the
        // asymmetry actually rested on and what replaced it. The row is first in the list because
        // it is what somebody scrolling a long roster came for.
        var everybody = EveryAgent();

        var rows = everybody.Count == 0
            ? peers
            : (IReadOnlyList<string>)[Participation.Everyone(everybody.Count), .. peers];

        var picking = Prompting.Choose(
            Participation.WhichPeer,
            inConversation is null ? Participation.StartTitle : Participation.RequestTitle,
            rows);

        var pick = Prompting.Open(app, picking);

        picking.Done += (_, chosen) =>
        {
            Prompting.Close(app, pick);

            if (chosen is null)
            {
                return;
            }

            if (Participation.IsEveryone(chosen, everybody.Count))
            {
                Open(null, everybody);

                return;
            }

            if (Resolve(chosen, out var peer))
            {
                Open(peer, null);
            }
        };

        return Task.CompletedTask;

        void Open(AgentName? peer, IReadOnlyList<AgentName>? all)
        {
        // **Everything that is prose is a long field, and the name is not.** Rule 8.1's three parts
        // are sentences and a request's text may run to the whole of rule 8.4's cap — the
        // owner wrote a long one into a field one row tall and could see about sixty characters of
        // it. A conversation name stays one line: it is a token that has to survive being written
        // down and read back, and Enter in it is how every form on this surface is finished.
        //
        // **And the name field applies its own rule as it is typed.** It carried
        // `RequiredProblem` alone, so the only thing that knew what may name a conversation was
        // the send — and the owner met that by naming one `tell everybody test`, writing the
        // message, and being told the rule with the form closed and their words gone.
        //
        // **Two fields, since 0.7.** There were five — an intent, the item, its kind, a done_when and the
        // conversation — and the owner, composing one, read the first four as the same thing asked four times.
        // §5.1 is now the ask and its conversation; a receiver that cannot scope it sends a Clarify. The ask keeps
        // its second line, because what a good one contains is worth saying where somebody is writing it.
        var fields = new List<FormField>
        {
            new(
                $"{Framing.Ask}. {Framing.AskWhy}",
                string.Empty,
                Participation.RequiredProblem,
                Long: true),
            new(
                "Conversation name — " + ConversationName.Rule,
                inConversation ?? Participation.Suggested(),
                Participation.ConversationProblem,
                Correct: ConversationName.Correct,
                Corrected: ConversationName.WasCorrected),
        };

        var form = new FormDialog(
            inConversation is null ? Participation.StartTitle : Participation.RequestTitle, fields);
        var token = Prompting.Open(app, form);

        form.Done += (_, values) =>
        {
            Prompting.Close(app, token);

            if (values is null)
            {
                Said = string.Empty;

                return;
            }

            // **The seat is asked for again here, and the one captured when the form opened is
            // thrown away.** A composer is open for as long as somebody takes to write, and the
            // idle sweep runs on every draw — so the seat can go while they are still typing. The
            // captured voice would go on speaking under a name the book had already released,
            // which is a transcript row attributed to somebody who had left: the one outcome the
            // test for this calls worse than the message not being sent.
            //
            // **It passed on Windows and failed on Linux**, because nothing here decided it — what
            // decided it was whether the attach stream had finished tearing down before the send
            // reached the broker. A guard that is really a race is not a guard.
            Queue(async cancellationToken =>
            {
                if (Voice() is not { } speaking || Held is not { } holding)
                {
                    Said = Participation.Gone;

                    return;
                }

                Said = (all, peer) switch
                {
                    // The hold goes in rather than only the voice, because sending to everybody is
                    // one send per recipient with the idle sweep running throughout: it re-reads the
                    // seat before each one and stops rather than finishing under a released name.
                    ({ } everybody, _) => await speaking.RequestEveryoneAsync(
                        holding, everybody, values[0], values[1], cancellationToken).ConfigureAwait(false),

                    (_, { } one) => await speaking.RequestAsync(one, values[0], values[1], cancellationToken)
                        .ConfigureAwait(false),

                    _ => Participation.Gone,
                };

                await SelectAsync(project, cancellationToken).ConfigureAwait(false);
            });
        };
        }
    }

    /// <summary>Everyone in this project a person could speak to.</summary>
    /// <returns>Their rows' names, in the order the roster shows them.</returns>
    /// <remarks>
    /// The person is left out because nobody asks themselves — recognised by role, since the person seat is the one
    /// row a surface speaks as (specs/023), whichever machine this terminal is on. Every other row is offered,
    /// including one that has never run — §12.3 routes spawn-on-demand through the catalogue, so a
    /// never-run-but-startable row is a legitimate thing to ask, and leaving it out would hide the
    /// agents a person most needs to wake.
    /// </remarks>
    private IReadOnlyList<string> Peers() =>
    [
        .. Agents.Standings
            .Where(row => !(AgentName.TryCreate(row.Name, out var name) && PersonSeat.Is(name)))
            .Select(row => row.Name)
            .OrderBy(name => name, StringComparer.Ordinal),
    ];

    /// <summary>Every agent in this project a broadcast reaches.</summary>
    /// <returns>The agents, in the order the roster shows them.</returns>
    /// <remarks>
    /// <para>
    /// <b>Agents, not people.</b> Every seat is left out — this person's and everybody else's. A
    /// seat is somebody at a desk rather than something that can be started, and "tell all the
    /// agents" addressed to a colleague is a different act from the one somebody chose.
    /// </para>
    /// <para>
    /// <b>A retired agent is not addressed rather than addressed and refused.</b> §14 refuses a
    /// Request to one, so including them would manufacture a refusal per retired row and
    /// report it beside the real ones — a broadcast that says "3 refused" about agents somebody
    /// deliberately retired reads as a fault.
    /// </para>
    /// <para>
    /// A row the roster shows and this cannot resolve is dropped rather than guessed at. Both
    /// spellings resolve (see <see cref="Resolve"/>), so what reaches here is a roster fault, and
    /// silently addressing the wrong agent is worse than telling one fewer.
    /// </para>
    /// </remarks>
    private IReadOnlyList<AgentName> EveryAgent() =>
    [
        .. Agents.Standings
            .Where(row => !row.Seat && !row.Retired)
            .OrderBy(row => row.Name, StringComparer.Ordinal)
            .Select(row => Resolve(row.Name, out var peer) ? (AgentName?)peer : null)
            .OfType<AgentName>(),
    ];

    /// <summary>Turns a row's name back into the agent it names.</summary>
    /// <param name="chosen">The row's name, as the roster spelled it.</param>
    /// <param name="peer">The agent.</param>
    /// <returns>Whether it named one.</returns>
    /// <remarks>
    /// A registration is spelled <c>role@machine</c> and a catalogue key is a role, so a row carries
    /// whichever its source knew. Both are accepted here, which is the one place that has to know —
    /// and because the value came from a list rather than from a person, a row this cannot resolve
    /// is a fault in the roster rather than a typing mistake.
    /// </remarks>
    private static bool Resolve(string chosen, out AgentName peer) =>
        AgentName.TryCreate(chosen, out peer) || MachineName.TryNameHere(chosen, out peer);

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

            if (_keyboard is { } keyboard)
            {
                keyboard.KeyDown -= AnyKey;
                _keyboard = null;
            }

            // Cancelled before the base disposes the views, so a read still in flight cannot draw
            // into a region that has gone. The seat book's streams are not this object's to end;
            // TerminalCommand owns those and ends them when the process does.
            _reading.Cancel();
            _reading.Dispose();

            // Already cancelled with the token it is linked to; released here so it is not left for
            // the finaliser.
            lock (_followGate)
            {
                _following?.Dispose();
                _following = null;
            }
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
/// <param name="Chord">
/// The keystroke that takes it where a letter cannot, or <see langword="null"/> for an ordinary letter.
/// </param>
/// <param name="Costs">
/// What taking it costs whoever it reaches, or null when it costs nobody anything.
/// </param>
/// <param name="Category">
/// What this action is about, or <see langword="null"/> when nobody has said. Null rather than a
/// default, because filing an action under a category somebody did not choose would be the surface
/// inventing a fact — the bar shows an unclassified action under <c>Other</c>, visibly, and a test
/// fails the build when anything lands there.
/// </param>
/// <param name="Group">
/// The run of related actions this belongs to, or <see langword="null"/> for none. A record rather than
/// a name so that a run's position is a property of the run: see <see cref="ActionGroup"/>.
/// </param>
/// <param name="Order">Where it sits inside its run.</param>
/// <remarks>
/// <para>
/// A record rather than a method per action so that the status line and the key handler read the
/// same list. Two copies of "what can be done here" is how a surface comes to offer something no
/// key takes, or take something nothing offered.
/// </para>
/// <para>
/// <b><see cref="Costs"/> is shown in the menu and never on the status line</b>, and the split is
/// what makes it affordable. The two composer actions need a clause each to be told apart — the
/// owner could not tell asking from announcing by their labels — and two clauses on the status
/// line is fifty columns that push every action behind <c>?</c> at any ordinary width. A menu is a
/// list somebody reads; a status line is a reminder of what they already know.
/// </para>
/// <para>
/// <b><see cref="Chord"/> is for an action whose key cannot be a letter</b>, which the room's input line made
/// necessary: every letter in that pane belongs to the text somebody is typing. Where it is set it is both what
/// dispatches the action and how the action is written, and <see cref="Key"/> is unused — one field would have
/// been better, and taking one would mean changing forty call sites and <c>Typed</c>'s cross-driver care with
/// it. What matters is that a chord action is still an action: it has a label, so it reaches the status line and
/// the menu, and `EveryKeyIsOfferedTests` holds this surface to naming every key a person could press.
/// </para>
/// </remarks>
internal sealed record RegionAction(
    char Key,
    string Label,
    Func<Task> Run,
    string? Costs = null,
    Key? Chord = null,
    ActionCategory? Category = null,
    ActionGroup? Group = null,
    int Order = 0)
{
    /// <summary>How this action's key is written, wherever it is offered.</summary>
    /// <remarks>Alt and the letter, since 2026-10-01; <see cref="Shell.Typed"/> says why.</remarks>
    public string Stroke => Chord?.ToString() ?? Shell.Alt(Key);

    /// <summary>The run this action falls in, named or not.</summary>
    /// <remarks>
    /// Defaulted here rather than at every call site, so a caller states a group only when it has one
    /// to state. <see cref="ActionGroup.Ungrouped"/> sorts last for the reason given on it.
    /// </remarks>
    public ActionGroup Grouping => Group ?? ActionGroup.Ungrouped;

    /// <summary>Whether a keystroke takes this action.</summary>
    /// <param name="key">What arrived.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool Answers(Key key) => Chord is { } chord
        ? key == chord
        : Shell.Typed(key, Key);
}
