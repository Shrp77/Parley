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

    /// <summary>What taking the other side of the one choice is called.</summary>
    internal const string OtherTitle = "Answer, or ask back instead";

    /// <summary>Sends what is written, as the preview said it would.</summary>
    /// <returns>When the send has been queued.</returns>
    /// <remarks>
    /// <b>The seat is read here rather than when the line was focused.</b> 0.18.9 found three call sites that
    /// captured it when a form opened and went on speaking under a name the book had released; the rule since is
    /// that the send asks. The plan is the draft's own, not recomputed: a second derivation between the preview
    /// and the send would be a second opinion about what was promised.
    /// </remarks>
    internal Task SendLine()
    {
        if (Held is not { } hold || Drafting is not { } draft || _place.Title is not { } conversation)
        {
            Said = Released;

            return Task.CompletedTask;
        }

        // An empty line is not checked here. The send answers it without reaching a broker, and one sentence
        // about it in one place cannot come to disagree with itself.
        draft.Text = Line.Written;

        Queue(async token =>
        {
            if (Voice() is not { } voice)
            {
                Said = Released;

                return;
            }

            var said = await voice.SendAsync(hold, draft, conversation, token).ConfigureAwait(false);

            Said = said;

            // Cleared only on a send that reached somebody. A refusal keeps what was written, which 0.25.1
            // recorded the cost of getting wrong: the cap was enforced after the form closed and took the
            // message with it.
            if (said.StartsWith("Sent to 0", StringComparison.Ordinal))
            {
                return;
            }

            Line.Written = string.Empty;

            if (Projects.Selected is { } project)
            {
                await OpenAsync(project, conversation, token).ConfigureAwait(false);
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>Takes the other side of the one choice a draft offers.</summary>
    /// <returns>When the preview has been redrawn.</returns>
    /// <remarks>
    /// Applied to every recipient in that state rather than to one, because the line addresses them together and
    /// there is no cursor among them to mean "this one". A person who wants them to differ sends twice, which is
    /// also what makes each send readable against its own outcome.
    /// </remarks>
    internal Task FlipOne()
    {
        if (Drafting is not { } draft)
        {
            return Task.CompletedTask;
        }

        foreach (var addressed in draft.Plan().Where(a => a.Choosable))
        {
            draft.Flip(addressed.Peer);
        }

        Line.Show(draft);
        Relay();

        return Task.CompletedTask;
    }

    /// <summary>What copying the open conversation is called.</summary>
    internal const string CopyTitle = "Copy this conversation to the clipboard";

    /// <summary>The key that copies it.</summary>
    internal const char CopyKey = 'c';

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
        "This machine's clipboard could not be written to. `parley export <conversation>` writes "
        + "the same transcript to a file.";

    /// <summary>What clearing the message line is called.</summary>
    internal const string ClearTitle = "Clear the message";

    /// <summary>What reading a message too long for the line is called.</summary>
    internal const string WholeTitle = "Show the whole message";

    /// <summary>The key that clears the message line.</summary>
    internal const char ClearKey = 'x';

    /// <summary>The key that shows a message the line could not hold.</summary>
    internal const char WholeKey = 'm';

    private readonly BrokerDirectory _directory;
    private readonly Plain _status = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };

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

    internal Shell(BrokerDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _directory = directory;

        Title = $"parley terminal {BuildIdentity.Version}";
        BorderStyle = LineStyle.None;

        Projects = new ProjectsRegion();
        Agents = new AgentsRegion();
        Conversations = new ConversationsRegion();
        Transcript = new TranscriptRegion();
        Room = new RoomRegion();
        Line = new LineRegion();

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
        Add(_message);
        Add(_status);
        Add(Projects);
        Add(Agents);
        Add(Conversations);
        Add(Transcript);
        Add(Room);
        Add(Line);

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
            only.Y = 0;
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
                pane.Y = 0;
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
            panes[0].Y = 0;
            panes[0].Width = _place.Viewing == Viewing.Conversation
                ? Dim.Absolute(Narrow)
                : Dim.Percent(40);
            panes[0].Height = Dim.Fill(1 + _messageRows);

            panes[1].X = Pos.Right(panes[0]);
            panes[1].Y = 0;
            panes[1].Width = Dim.Fill();
            panes[1].Height = Dim.Fill(1 + _messageRows);
        }

        _arranged = true;

        Focus(FocusedRegion);
    }

    /// <summary>How wide the room's own column is, in columns.</summary>
    /// <remarks>
    /// Wide enough for an agent name and the clause beside it -- "billing@matrixmaster -- waiting on you" is
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
        // The message's actions sit between the ways out and the region's own, and drop off the
        // ladder before either -- the menu holds them, and unlike Escape they are about something
        // that is already on the screen in front of the person.
        //
        // **`?` is on the full line too, which it was not.** The rung that lists everything named
        // no menu, so on a wide terminal -- where everything fits -- the one route a pointer has
        // to any action was on no screen at all. contracts/surface.md asks that no action exist
        // only as a key, and a status line is not clickable. Twelve columns at the width where
        // there is most room for them.
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
            placed + mail + back + quit + about + listed + menu + build,
            placed + mail + back + quit + about + listed + menu,
            placed + mail + back + quit + about + menu + build,
            placed + mail + back + quit + about + menu,
            placed + mail + back + quit + menu + build,
            placed + mail + back + quit + menu,
            placed + mail + menu,
            focus + mail + menu,
            focus + mail + brief,
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
        .. SaidMore ? new[] { new RegionAction(WholeKey, WholeTitle, Whole) } : [],
        .. Said.Length > 0 ? new[] { new RegionAction(ClearKey, ClearTitle, Clear) } : [],
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
    private static string Caption(RegionAction action) => $"{action.Key}: {action.Label}";

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

        // **A conversation opens on its transcript, not on the room beside it.** The room is context; the
        // transcript is what somebody opened a conversation to read, and it is where every action on this
        // view already lived. Focusing the left column because it is drawn first would put the cursor on
        // the furniture.
        Focus(Opening(place));

        return Task.CompletedTask;
    }

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
            new RegionAction('y', HealthTitle, ShowHealth),
            new RegionAction('l', Files.ProjectTitle, ShowFiles),
            new RegionAction('p', Pruning.Title, PruneSelected),
            new RegionAction('g', FollowLogTitle, FollowLog),
            new RegionAction('e', Onboarding.Title, Enrol),
            new RegionAction('b', OpenConsoleTitle, OpenConsole),
        ],

        AgentsRegion =>
        [
            new RegionAction('a', Adding.Title, Add),

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

            // Taking the seat under the cursor, offered rather than left as a bare Enter. The
            // surface's rule is that no action exists only as a key, and a row carrying somebody's
            // own name is exactly where they will press Enter before reading anything.
            .. Seats is not null && Held is null && Agents.Selected is { Seat: true } mine
                && Mine(mine.Name)
                ? new[] { new RegionAction('t', TakeThisSeat + mine.Name, SeatSelected) }
                : Array.Empty<RegionAction>(),

            // What the operator told this agent, and what it said back. Never for a seat: a seat
            // is a person, and nobody instructs themselves.
            .. Agents.Selected is { Seat: false } told
                ? new[]
                {
                    new RegionAction('w', ToldTitle + told.Name, ShowTold),
                    new RegionAction('o', Instructing.Title + told.Name, InstructSelected),
                }
                : Array.Empty<RegionAction>(),

            .. Seats is null
                ? Array.Empty<RegionAction>()
                : Held is null
                    ? [new RegionAction('j', AgentsRegion.JoinTitle, Join)]
                    : new[] { new RegionAction('l', LeaveSeatTitle, LeaveSeat) },

            // **Taking delivery lives here as well as in a transcript, and that is the correction.** It
            // was offered only inside a conversation, so collecting what you had been sent meant opening
            // one -- and what is waiting is frequently in a conversation you have never seen, which is
            // exactly the one you would not think to open. This pane is where the seat is.
            .. Held is not null
                ? new[] { new RegionAction('w', Participation.DeliveryTitle, TakeDelivery) }
                : Array.Empty<RegionAction>(),
        ],

        LineRegion =>
        [
            .. Held is not null
                ? new[]
                {
                    new RegionAction('\r', SendTitle, SendLine, Owing.Requesting),
                    new RegionAction('\t', OtherTitle, FlipOne),
                }
                : new[] { new RegionAction('j', AgentsRegion.JoinTitle, Join) },
        ],

        // The conversations half of a project view. Sorting and the archived toggle are view
        // state; archiving is reversible and asks nothing; destroying is behind the full door.
        ConversationsRegion =>
        [
            new RegionAction('s', SortTitle, SortConversations),
            new RegionAction('v', _showArchived ? HideArchivedTitle : ShowArchivedTitle, ToggleArchived),

            // Starting one belongs here rather than in a transcript of a different one, which is
            // this method's own rule read straight. Needs a seat for the reason every other way of
            // speaking does: without one there is no identity to speak as.
            .. Held is not null
                ? new[]
                {
                    new RegionAction('k', Participation.StartTitle, Start, Owing.Requesting),
                }
                : Seats is not null
                    ? new[] { new RegionAction('k', Participation.StartSeatFirstTitle, StartSeatFirst, Owing.Requesting) }
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

            new RegionAction('y', HealthTitle, ShowHealth),
            new RegionAction('b', OpenConsoleTitle, OpenConsole),
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
                ? new[] { new RegionAction(CopyKey, CopyTitle, Copy) }
                : Array.Empty<RegionAction>(),
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

        // The message's actions are in the menu for the reason back and quit are: no action on this
        // surface exists only as a key, and a status line that has fallen back to `?: actions` has
        // taken their captions off the screen.
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

            if (value is null)
            {
                Mail = null;
            }
        }
    }

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

        var was = Held;
        Held = held;

        // The count belongs to the seat, so the watch starts with one; setting Held drops it again.
        if (held is not null && was is null)
        {
            WatchMail();
        }
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
    private Task Enrol() =>
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
        var room = Math.Clamp(height - 1 - LeastRegionRows, 1, MessageDeepest);

        IReadOnlyList<string> pieces = _said.Length == 0
            ? [string.Empty]
            : TranscriptRegion.Wrap(_said, width);

        SaidMore = pieces.Count > room;

        IReadOnlyList<string> shown = SaidMore
            ? [.. pieces.Take(room - 1), $"{WholeKey}: {WholeTitle}"]
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
                foreach (var pane in Panes)
                {
                    pane.Height = Dim.Fill(1 + _messageRows);
                }
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
                new Catalogue(ManifestPath),
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

            await Withdrawing.OpenAsync(
                app, client, row, agent, owed, project.Name.Value, project.NodeConfig, Queue,
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

    /// <summary>What taking the seat under the cursor is called, before the name.</summary>
    internal const string TakeThisSeat = "Take the seat: ";

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

    /// <summary>What a retired seat's dialog is called.</summary>
    internal const string RetiredSeatTitle = "That seat is retired";

    /// <summary>Taking a retired seat back.</summary>
    internal const string ReinstateAndSit = "Reinstate it and take it";

    /// <summary>Leaving a retired seat alone.</summary>
    internal const string LeaveRetired = "Leave it retired";

    /// <summary>
    /// Takes the seat the person is standing on.
    /// </summary>
    /// <returns>When the seat has been taken, or the dialog that has to come first is open.</returns>
    /// <remarks>
    /// <para>
    /// <b>Enter on a seat takes it, which is what somebody expects of a row with their own name on
    /// it.</b> Before this, the only way in was `j` and typing the name back — which works, and
    /// which nobody guesses when the name is already on the screen under the cursor.
    /// </para>
    /// <para>
    /// <b>A retired seat asks first, and the asking is the point.</b> The broker refuses a retired
    /// agent's attach outright, so Enter would otherwise be a keystroke that reports a failure and
    /// leaves the person to find `i`. Reinstating is an operator operation (§10.2) and this surface
    /// does not take one silently — but a seat is the person's own name rather than another agent's,
    /// so what it needs is a sentence and a deliberate answer, not a name typed back. That is the
    /// same distinction the withdrawal doors already draw between describing and destroying.
    /// </para>
    /// </remarks>
    internal Task SeatSelected()
    {
        // `Current` rather than `Projects.Selected`, which is the fourth time that distinction has
        // mattered here. The projects region is off screen in a project view and its selection is
        // null in any fixture that never filled it, so a seat taken through it would be a seat
        // taken in no project at all.
        if (App is not { } app
            || Seats is null
            || Held is not null
            || Current is not { } project
            || Agents.Selected is not { Seat: true } row
            || !Mine(row.Name))
        {
            return Task.CompletedTask;
        }

        if (Agents.SelectedStanding is { Retired: true })
        {
            var dialog = Prompting.Choose(
                RetiredSeatTitle,
                $"'{row.Name}' is a seat an operator withdrew, so the broker will refuse it as it "
                + "stands. Reinstating puts the name back in the registry and then seats you in it; "
                + "nothing else about the deployment changes.",
                [LeaveRetired, ReinstateAndSit]);

            var token = Prompting.Open(app, dialog);

            dialog.Done += (_, chosen) =>
            {
                Prompting.Close(app, token);

                if (chosen != ReinstateAndSit)
                {
                    return;
                }

                Queue(async cancellationToken =>
                {
                    // Through Withdrawing, which is the one file an operator operation is reached
                    // from. SurfaceTests holds that, and it is the check rather than a convention.
                    await Withdrawing.ReinstateSeatAsync(
                        new BrokerClient(_directory.ClientFor(project)), row.Name, cancellationToken)
                        .ConfigureAwait(false);

                    await TakeSeatAsync(project, Person(row.Name), cancellationToken)
                        .ConfigureAwait(false);
                });
            };

            return Task.CompletedTask;
        }

        Queue(cancellationToken => TakeSeatAsync(project, Person(row.Name), cancellationToken));

        return Task.CompletedTask;
    }

    /// <summary>The person's half of a seat's name.</summary>
    /// <param name="seat">The agent name a seat row carries.</param>
    /// <returns>The role, which is what a person types when they join.</returns>
    /// <remarks>
    /// <b>A seat is named by the person and the machine is added.</b> <c>Seat.TryRead</c> takes
    /// <c>arvind</c> and makes <c>arvind@this-machine</c>; handing it <c>arvind@test</c> whole is
    /// refused as not a name. The row carries the whole thing, so the halves have to be separated
    /// here — a first draft passed the row's name straight through and every Enter reported that
    /// the name on the screen was not a name.
    /// </remarks>
    private static string Person(string seat) =>
        AgentName.TryCreate(seat, out var name) ? name.Role : seat;

    /// <summary>Whether a seat on this row would be seated under the name it shows.</summary>
    /// <param name="seat">The agent name a seat row carries.</param>
    /// <returns>Whether its machine is this one.</returns>
    /// <remarks>
    /// <b>A seat names a machine, and taking one only ever seats you on yours.</b> Pressing Enter
    /// on <c>arvind@somewhere-else</c> would seat <c>arvind@here</c> — a different agent from the
    /// row under the cursor, appearing somewhere else in the list, while the row pressed carried on
    /// belonging to somebody at another desk. Offering nothing is the honest answer: that seat is
    /// not this person's to take.
    /// </remarks>
    private static bool Mine(string seat) =>
        AgentName.TryCreate(seat, out var name)
        && string.Equals(name.Machine, MachineName.Current, StringComparison.Ordinal);

    /// <summary>Seats the person under one name and says what happened.</summary>
    /// <param name="project">The project to sit in.</param>
    /// <param name="name">The seat's name.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>When the seat is taken or refused.</returns>
    /// <remarks>
    /// Shared by the key and by the dialog that types a name, so both report the same way and both
    /// go through <c>SeatBook</c>'s guard. A second path that seated somebody without it is how the
    /// one thing this surface actually protects would be lost.
    /// </remarks>
    private async Task TakeSeatAsync(
        ProjectSettings project, string name, CancellationToken cancellationToken)
    {
        var registry = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(_directory.ClientFor(project))
                .RegistryAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        var (hold, said) = await AgentsRegion.JoinAsync(
            Seats!, project, registry.Value, name, cancellationToken).ConfigureAwait(false);

        var first = Held is null && hold is not null;

        Held = hold ?? Held;
        Me = hold?.Seat ?? Me;
        Said = said;

        if (first)
        {
            WatchMail();
        }

        await SelectAsync(project, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens the dialog that takes a seat.</summary>
    /// <returns>When the dialog has been opened.</returns>
    internal Task Join() => Join(then: null);

    /// <summary>Asks for a name and takes a seat under it, then does something with the seat.</summary>
    /// <param name="then">What to do once seated, or null for nothing; not done when the seat was refused.</param>
    /// <returns>When the dialog is open.</returns>
    private Task Join(Func<Task>? then)
    {
        if (App is not { } app || Seats is null || Projects.Selected is not { } project)
        {
            return Task.CompletedTask;
        }

        var dialog = Prompting.Text(
            AgentsRegion.JoinTitle,
            AgentsRegion.JoinQuestion,
            problem: AgentsRegion.NameProblem,

            // The correction is `Seat`'s own, not this surface's: it is the same rule `TryRead`
            // applies a moment later, and the console's join form applies it too. A copy here
            // would be a third opinion about what a person may call themselves.
            correct: Seat.Correct,
            corrected: Seat.WasCorrected);

        var token = Prompting.Open(app, dialog);

        dialog.Done += (_, typed) =>
        {
            Prompting.Close(app, token);

            if (typed is null)
            {
                return;
            }

            // The same path the key takes, so a name typed and a row chosen are seated alike and
            // reported alike.
            Queue(async cancellationToken =>
            {
                await TakeSeatAsync(project, typed, cancellationToken).ConfigureAwait(false);

                if (then is not null && Held is not null)
                {
                    await then().ConfigureAwait(false);
                }
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

    /// <summary>Takes a seat, and then starts a conversation from it.</summary>
    /// <returns>When the seat dialog is open.</returns>
    internal Task StartSeatFirst()
    {
        Said = Participation.SeatFirst;

        return Join(then: Start);
    }

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

        // Redrawn here rather than left to the next thing that happens. Clearing the count is not enough on its
        // own: the status line is only rebuilt when something asks it to, so leaving a seat on a surface with no
        // project selected left the number on screen under a seat that had gone.
        Relay();

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

        // **Enter on a seat takes it**, which is what somebody expects of a row carrying their own
        // name. The agents list raises no `Accepting` of its own, so there is nothing here to
        // preempt -- the reason the projects view is singled out above does not apply to it.
        //
        // Only a seat. Enter on an agent is left alone: there is no one obvious thing it should do
        // to a peer, and guessing would be a key that does something different from what the
        // status line says it does.
        if (key == Key.Enter && Panes[FocusedRegion] is AgentsRegion
            && Agents.Selected is { Seat: true })
        {
            _ = SeatSelected();

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

        // **After the region's, so a pane's own letter always wins.** These two are on every
        // screen, and a global key that shadowed a region's would take an action away from
        // wherever it was added later — silently, and only on the screens that had it.
        foreach (var action in Messaging())
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

    /// <summary>Opens the dialogs that apply the retention policy of the selected project now.</summary>
    /// <returns>When the first dialog is open.</returns>
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

        await FillRoomAsync(project, read.Value, cancellationToken).ConfigureAwait(false);

        // Followed only once it has been read. A conversation that could not be read already says why,
        // and asking a broker that did not answer every few seconds would say it again each time.
        if (read.Problem is null)
        {
            StartFollowing(project, conversationId);
        }
    }

    /// <summary>Fills the room's column from the conversation and the broker's wait-for graph.</summary>
    /// <param name="project">The project.</param>
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

        var pairs = Pairing.From(export, waiting.Value ?? [], hold.Seat.Name);

        Room.Show(pairs, Outstanding(pairs), seated: true);

        // Rebuilt from the pair state that was just read, keeping whichever answer-or-ask-back choices the
        // person had already made about peers that are still in the same state.
        var was = Drafting;

        Drafting = new Draft(pairs) { Text = Line.Written };

        if (was is not null)
        {
            foreach (var peer in pairs.Where(pair => pair.Turn == Turn.Mine && !was.Answering(pair.Peer)))
            {
                Drafting.Flip(peer.Peer);
            }
        }

        Line.Show(Drafting);
    }

    /// <summary>What this person must address in the conversation on screen.</summary>
    /// <param name="pairs">The pair state for every participant.</param>
    /// <returns>One item per thing owed, in the order the participants are listed.</returns>
    /// <remarks>
    /// Read from the same pair state the input line derives its acts from, so the column and the line cannot
    /// disagree about what is outstanding. Two sources for one fact is how a surface comes to promise something
    /// it will not do.
    /// </remarks>
    private static IReadOnlyList<OutstandingItem> Outstanding(IReadOnlyList<PairState> pairs) =>
        [.. pairs
            .Where(pair => pair.Turn == Turn.Mine && pair.OpenExchange is not null)
            .Select(pair => new OutstandingItem(
                pair.Peer,
                $"{pair.Peer.Value} — answer or ask back",
                pair.OpenExchange!.Value,
                null))];

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
    private Task Compose(string? inConversation)
    {
        if (App is not { } app || Voice() is not { } voice || Projects.Selected is not { } project)
        {
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
    /// Their own seat is left out because nobody asks themselves; every other row is offered,
    /// including one that has never run — §12.3 routes spawn-on-demand through the catalogue, so a
    /// never-run-but-startable row is a legitimate thing to ask, and leaving it out would hide the
    /// agents a person most needs to wake.
    /// </remarks>
    private IReadOnlyList<string> Peers() =>
    [
        .. Agents.Standings
            .Where(row => !(row.Seat && Mine(row.Name)))
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
/// <param name="Costs">
/// What taking it costs whoever it reaches, or null when it costs nobody anything.
/// </param>
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
/// </remarks>
internal sealed record RegionAction(char Key, string Label, Func<Task> Run, string? Costs = null);
