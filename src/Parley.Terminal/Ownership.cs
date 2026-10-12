using System.Globalization;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Core;
using Parley.Setup;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Recording what a person says an agent owns (SPEC.md §10.2 <c>record_ownership</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Which words were whose is recorded.</b> A surface that could not tell a person's words from
/// an agent's would make the record worth nothing. There are three routes and this file holds all three: a line typed
/// here with no ask is <see cref="OwnershipSource.Composed"/>; a line an agent proposed and somebody
/// accepted unchanged is <see cref="OwnershipSource.Proposed"/>, carrying the conversation it was
/// agreed in; the same line changed before accepting is <see cref="OwnershipSource.Edited"/>.
/// </para>
/// <para>
/// <b>Which of the last two happened is a comparison, not a flag.</b> The field opens holding the
/// agent's own sentence, so accepting is one keystroke, and what is recorded is decided by comparing
/// what was sent with what came back — asking a person to declare whether they had edited something
/// would be asking them to remember what they had just done.
/// </para>
/// <para>
/// <b>It has to say that nothing an asker reads has changed.</b> The registry is filled from the
/// launch environment when the agent attaches (§12.3), so accepting a line is a decision until the
/// agent next starts. A screen reporting only "recorded" would be true about the record and false
/// about the deployment, which is the shape 0.25.2 records paying for elsewhere.
/// </para>
/// </remarks>
internal static class Ownership
{
    /// <summary>The door this operation passes through.</summary>
    internal const string Title = "Say what this agent owns";

    /// <summary>What the field asks for.</summary>
    internal const string TypeIt = "One line, read by every agent that lists the roster:";

    /// <summary>Said when the person gives nothing.</summary>
    internal const string Nothing = "Nothing was typed, so nothing was recorded.";

    /// <summary>The door asking an agent passes through.</summary>
    internal const string AskTitle = "Ask what this agent owns";

    /// <summary>The choice that asks, which is never the one under the cursor.</summary>
    internal const string Ask = "Ask it, and spend the turn";

    /// <summary>The choice that changes nothing, which every dialog here opens on.</summary>
    internal const string Leave = "Do not ask";

    /// <summary>Said when the person declines.</summary>
    internal const string Left = "Nothing was asked and nothing was spent.";

    /// <summary>The door asking every agent passes through.</summary>
    internal const string AskEveryTitle = "Ask every agent what it owns";

    /// <summary>The choice that asks all of them.</summary>
    internal const string AskThem = "Ask them all, and spend the turns";

    /// <summary>Said when there is nobody to ask.</summary>
    /// <remarks>
    /// Distinguished from a refusal, because a deployment with no agents registered yet is an ordinary
    /// first afternoon rather than a fault — and telling somebody nothing could be asked would send
    /// them looking for a broken broker.
    /// </remarks>
    internal const string NobodyToAsk =
        "There is no agent in this project to ask. An agent appears here once it has attached and "
        + "registered, which is its own act as it starts.";

    /// <summary>The door the restart offer passes through.</summary>
    internal const string RestartTitle = "Make this the line peers read";

    /// <summary>The choice that restarts the agent so peers read the line.</summary>
    internal const string RestartIt = "Restart it now, so peers read it";

    /// <summary>The choice that leaves the line waiting for the agent's next start.</summary>
    internal const string LeaveIt = "Leave it until it next starts";

    /// <summary>Said when the restart was asked for and the node took it.</summary>
    internal const string Restarting =
        "Asked its node to start it. It registers as it attaches, and the line peers read is "
        + "whatever its launch configuration carries by then.";

    /// <summary>Said when the restart was declined.</summary>
    internal const string NotRestarted =
        "Left as it is. Peers read the old line until the agent next starts, and the roster says so.";

    /// <summary>The door accepting what an agent proposed passes through.</summary>
    internal const string AcceptTitle = "Accept what this agent proposed";

    /// <summary>Said when the discussion holds nothing the agent has written.</summary>
    /// <remarks>
    /// Distinguished from an agent that answered badly, which comes back as it was written: this is
    /// the agent having said nothing at all, which is a fact about the deployment rather than about
    /// the answer, and 0.29.1 records what collapsing those two costs.
    /// </remarks>
    internal const string NothingProposed =
        "This agent has not answered yet, so there is nothing to accept. Its answer will appear in "
        + "this conversation when it takes its next turn.";

    /// <summary>Said when the discussion does not say whose line it is about.</summary>
    /// <remarks>
    /// Reachable when two agents in one discussion share a role, which is an ordinary deployment —
    /// and what follows an acceptance is a write to somebody's launch configuration, so being shown
    /// one of the two would be the surface choosing whose file to edit.
    /// </remarks>
    internal const string NoSubject =
        "This conversation does not say which single agent it is about, so nothing can be accepted "
        + "from it. Say the line from the agents pane instead, where the agent is the row.";

    /// <summary>Has Parley ask the agent what it owns again (§10.2 <c>ask_again</c>).</summary>
    /// <param name="app">The application the dialog opens in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="agent">Who to ask.</param>
    /// <param name="queue">Runs the read and the ask, so the caller can wait for them.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the read has been queued.</returns>
    /// <remarks>
    /// <para>
    /// <b>No seat, because Parley asks</b> (022). 020 sent this as the person's own Request, so it needed a seat, and
    /// the owner met "an ask is a message, so it is sent as somebody, and no seat is held here" on the one action they
    /// reached for. The facilitator is the somebody now.
    /// </para>
    /// <para>
    /// <b>The words are shown before the turn is spent</b> (FR-003), and they are the words Parley will send: the
    /// switch is read first, because the question tells the agent what becomes of its answer and that differs with it.
    /// </para>
    /// <para>
    /// The machine rule of <see cref="OwnershipLine.NotHere"/> deliberately does not apply: asking is not writing a
    /// file, and a broker reaches agents on machines this surface never touches.
    /// </para>
    /// </remarks>
    internal static Task AskAsync(
        IApplication app,
        BrokerClient client,
        AgentName agent,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        queue(async t =>
        {
            var read = await BrokerDirectory.ReadAsync(
                async () => await client.FacilitationAsync(t).ConfigureAwait(false)).ConfigureAwait(false);

            if (!read.Reachable)
            {
                await done($"Nothing was asked: {Safe.Text(read.Problem!)}").ConfigureAwait(false);

                return;
            }

            var asking = Prompting.Choose(AskTitle, Asked(agent, read.Value!.AutoAccept), [Leave, Ask]);
            var open = Prompting.Open(app, asking);

            asking.Done += (_, chosen) =>
            {
                Prompting.Close(app, open);

                if (!string.Equals(chosen, Ask, StringComparison.Ordinal))
                {
                    _ = done(Left);

                    return;
                }

                queue(async sending =>
                {
                    var asked = await BrokerDirectory.ReadAsync(
                        async () => await client.AskAgainAsync(agent, sending).ConfigureAwait(false))
                        .ConfigureAwait(false);

                    await done(AskedOrNot(agent, asked)).ConfigureAwait(false);
                });
            };
        });

        return Task.CompletedTask;
    }

    /// <summary>What to say once Parley was asked to ask.</summary>
    /// <param name="agent">Who was to be asked.</param>
    /// <param name="asked">What came back.</param>
    /// <returns>One line, safe to draw.</returns>
    private static string AskedOrNot(AgentName agent, ProjectRead<FacilitationAction> asked) =>
        !asked.Reachable
            ? $"Nothing was asked: {Safe.Text(asked.Problem!)}"
            : asked.Value!.Done
                ? $"Parley asked {Safe.Text(agent.Value)} what it owns. Its answer shows in the ownership review."
                : $"Nothing was asked: {Safe.Text(asked.Value.Problem!)}";

    /// <summary>Has Parley ask every agent in the project what it owns again.</summary>
    /// <param name="app">The application the dialog opens in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="agents">Who to ask.</param>
    /// <param name="queue">Runs the asks, so the caller can wait for them.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the dialog is open, or the refusal has been said.</returns>
    /// <remarks>
    /// <para>
    /// <b>The cost is stated and answered before the first question is sent</b> (FR-007): a turn per agent cannot be
    /// un-spent. Parley already asks every new agent on its own, so this is for asking again, and says so.
    /// </para>
    /// <para>
    /// An agent whose answer is still being decided is refused and named rather than asked over: asking again then
    /// would lose an answer nobody has looked at. Nothing is waited on per agent — each ask is one round trip and the
    /// answers arrive later — which is 0.25.1's rule satisfied by there being nothing to defer.
    /// </para>
    /// </remarks>
    internal static Task AskEveryAsync(
        IApplication app,
        BrokerClient client,
        IReadOnlyList<AgentName> agents,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        if (agents.Count == 0)
        {
            _ = done(NobodyToAsk);

            return Task.CompletedTask;
        }

        var asking = Prompting.Choose(AskEveryTitle, Costing(agents), [Leave, AskThem]);
        var open = Prompting.Open(app, asking);

        asking.Done += (_, chosen) =>
        {
            Prompting.Close(app, open);

            if (!string.Equals(chosen, AskThem, StringComparison.Ordinal))
            {
                _ = done(Left);

                return;
            }

            queue(async t =>
            {
                var asked = 0;
                var refused = new List<string>();

                foreach (var agent in agents)
                {
                    var outcome = await BrokerDirectory.ReadAsync(
                        async () => await client.AskAgainAsync(agent, t).ConfigureAwait(false)).ConfigureAwait(false);

                    if (outcome is { Reachable: true, Value.Done: true })
                    {
                        asked++;
                    }
                    else
                    {
                        refused.Add(agent.Value);
                    }
                }

                await done(AskedEvery(asked, refused)).ConfigureAwait(false);
            });
        };

        return Task.CompletedTask;
    }

    /// <summary>What to say once Parley was asked to ask each of them.</summary>
    /// <param name="asked">How many were asked.</param>
    /// <param name="refused">Who was not.</param>
    /// <returns>One line, safe to draw.</returns>
    private static string AskedEvery(int asked, IReadOnlyList<string> refused)
    {
        if (refused.Count == 0)
        {
            return $"Parley asked {asked} agent(s) what they own. Their answers show in the ownership review.";
        }

        var named = string.Join(", ", refused.Take(Named).Select(Safe.Text));
        var more = refused.Count > Named ? $" and {refused.Count - Named} more" : string.Empty;

        return $"Parley asked {asked} agent(s). Not asked: {named}{more} -- each has an answer still being " +
               "decided, or could not be reached.";
    }

    /// <summary>What the person reads before spending a turn on each of them.</summary>
    /// <param name="agents">Who will be asked.</param>
    /// <returns>The description.</returns>
    /// <remarks>
    /// Names them as well as counting them, up to a point: a count alone is a number somebody has to
    /// trust, and the list is what tells them the set is the one they meant. Abbreviated after a handful
    /// for the reason 0.25.1 records — a report naming nineteen agents ran off the screen at the half
    /// that said what to do about it.
    /// </remarks>
    internal static string Costing(IReadOnlyList<AgentName> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var named = agents.Take(Named).Select(a => Safe.Text(a.Value));

        var who = agents.Count <= Named
            ? string.Join(", ", named)
            : string.Join(", ", named) + $" and {agents.Count - Named} more";

        return $"""
                {who}

                {Safe.Lines(OwnershipAsk.Costs(agents.Count))}

                Parley asks each of them again, separately and in a conversation of its own. What becomes
                of each answer depends on this project's Auto Accept switch, and every answer shows in the
                ownership review either way.
                """;
    }

    /// <summary>How many agents are named before the rest are counted.</summary>
    /// <remarks>
    /// Four, as every other list on this surface uses — the number is 0.25.1's, and the reason it is
    /// stated here rather than shared is that <c>SeatVoice</c>'s copy is private to the report it writes.
    /// If a third place needs it, it moves.
    /// </remarks>
    private const int Named = 4;

    /// <summary>What the person reads before Parley asks.</summary>
    /// <param name="agent">Who will be asked.</param>
    /// <param name="autoAccept">The project's Auto Accept switch, which chooses the words.</param>
    /// <returns>The description.</returns>
    /// <remarks>
    /// Shows the question itself rather than describing it. The whole of FR-003 is that the agent is told what its
    /// answer is for, and a surface summarising those words would be the one place the promise could quietly stop
    /// being true.
    /// </remarks>
    internal static string Asked(AgentName agent, bool autoAccept)
    {
        var becomes = autoAccept
            ? "Auto Accept is on, so its answer becomes its line unless it cannot be one."
            : "Auto Accept is off, so its answer waits for a person in the ownership review.";

        return $"""
                {Safe.Text(agent.Value)}

                Parley will ask it again. This costs {Safe.Text(agent.Role)} one turn, and opens a
                conversation of its own. {becomes}

                It will be sent these words:

                {Safe.Lines(Facilitation.Question(autoAccept))}
                """;
    }

    /// <summary>Opens the dialogs that record an ownership line.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="agent">Whose line it is.</param>
    /// <param name="workspace">The repository holding it, as the registry records it.</param>
    /// <param name="project">The project whose launch entry carries the line.</param>
    /// <param name="queue">Runs the operation, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the first dialog is open.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        AgentName agent,
        string? workspace,
        string? project,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        // **Refused before the form opens, not after it is filled in.** 0.25.2 records what the other
        // order costs: a rule met after the form closed took somebody's words with it. Nothing about
        // this machine or that repository will change while a person is typing.
        if (OwnershipLine.NotHere(agent) is { } elsewhere)
        {
            _ = done(Safe.Text(elsewhere));

            return Task.CompletedTask;
        }

        var typing = Prompting.Text(Title, Described(agent));
        var typed = Prompting.Open(app, typing);

        typing.Done += (_, said) =>
        {
            Prompting.Close(app, typed);

            var statement = (said ?? string.Empty).Trim();

            if (statement.Length == 0)
            {
                _ = done(Nothing);

                return;
            }

            queue(t => AcceptAsync(
                app,
                client,
                agent,
                workspace,
                project,
                new OwnershipAcceptance(statement, OwnershipSource.Composed, null, null),
                t,
                queue,
                done));
        };

        return Task.CompletedTask;
    }

    /// <summary>What the person reads before they type.</summary>
    /// <param name="agent">Whose line it is.</param>
    /// <returns>The description.</returns>
    /// <remarks>
    /// Names the cost rather than a length, because there is no length any more: the bound came off
    /// at 0.33.0 and what is left is the reason to be brief. A field that said nothing about cost
    /// would leave somebody no reason not to write a paragraph every peer then reads.
    /// </remarks>
    internal static string Described(AgentName agent) =>
        $"""
         {Safe.Text(agent.Value)}

         {TypeIt}

         Every agent in this deployment reads it whenever anybody lists the roster, so it is read far
         more often than it is written, and it costs each of them context every time. Say what this
         agent owns and what it does not -- the exclusion is the half that stops somebody asking the
         wrong agent.

         This records your words as yours. It takes effect when the agent next starts, because the
         line itself comes from its launch configuration.
         """;

    /// <summary>Reads what the agent proposed, and offers it for acceptance.</summary>
    /// <param name="app">The application the dialog opens in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="conversationId">The discussion, which is recorded with the acceptance.</param>
    /// <param name="roster">
    /// The project's registry as it was last read. It supplies three things once the discussion has said
    /// whose line it is: the repository the write lands in, the line peers read today, and what the other
    /// agents already claim (FR-017).
    /// </param>
    /// <param name="project">The project whose launch entry carries the line.</param>
    /// <param name="queue">Runs the acceptance, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <param name="cancellationToken">Cancels reading the discussion.</param>
    /// <returns>When the dialog is open, or the refusal has been said.</returns>
    /// <remarks>
    /// <para>
    /// <b>The discussion is read again here rather than taken off the screen.</b> A transcript is
    /// followed every few seconds, so what was drawn is what was true a moment ago — and an agent
    /// that revised its answer in that moment would have the sentence it retracted written to its
    /// launch configuration. The proposal a person accepts is the latest one at the instant they
    /// accept it, which is 0.30.1's rule taken to its conclusion.
    /// </para>
    /// <para>
    /// <b>Whose line it is comes out of the discussion, not off the screen.</b> One read answers both
    /// questions — who was asked, and what they last said — where taking the agent from the cursor
    /// would let a person accept one agent's sentence into another agent's file if the selection had
    /// moved. Two reads on one keystroke is also one wasted export and two chances to disagree, which
    /// 017 records removing from the conversations list.
    /// </para>
    /// <para>
    /// <b>The machine refusal is applied before the dialog opens</b>, because this half writes a
    /// file on the agent's machine (0.25.2) — and after the subject is known rather than before,
    /// since until the discussion has been read there is no agent to apply it to.
    /// </para>
    /// </remarks>
    internal static async Task ReviewAsync(
        IApplication app,
        BrokerClient client,
        string conversationId,
        IReadOnlyList<AgentRecord> roster,
        string? project,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var read = await BrokerDirectory.ReadAsync(
            async () => await client
                .ExportAsync(conversationId, cancellationToken)
                .ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!read.Reachable)
        {
            await done($"The discussion could not be read: {Safe.Text(read.Problem!)}")
                .ConfigureAwait(false);

            return;
        }

        if (OwnershipProposal.Subject(read.Value!.Messages) is not { } agent)
        {
            await done(NoSubject).ConfigureAwait(false);

            return;
        }

        if (OwnershipLine.NotHere(agent) is { } elsewhere)
        {
            await done(Safe.Text(elsewhere)).ConfigureAwait(false);

            return;
        }

        var known = roster.FirstOrDefault(record => record.Name == agent);
        var workspace = known?.Workspace;
        var live = known?.Owns;

        if (OwnershipProposal.Latest(read.Value.Messages, agent) is not { } proposal)
        {
            await done(NothingProposed).ConfigureAwait(false);

            return;
        }

        var typing = Prompting.Text(
            AcceptTitle,
            Reviewed(agent, live, proposal, Roster.Neighbours(roster, agent)),
            proposal.Statement,
            problem: typed => OwnershipStatement.IsValid(typed, out var wrong) ? null : wrong);

        var open = Prompting.Open(app, typing);

        typing.Done += (_, said) =>
        {
            Prompting.Close(app, open);

            var statement = (said ?? string.Empty).Trim();

            if (statement.Length == 0)
            {
                _ = done(Nothing);

                return;
            }

            // **The agent's own length, never the accepted one, and 0.29.0's lens 2 is why.** The
            // evidence table exists so a bound can one day be set from what agents actually write;
            // a length a person typed is not that, and writing it would poison the only record
            // FR-026 asks for.
            var acceptance = new OwnershipAcceptance(
                statement,
                Chosen(statement, proposal.Statement),
                proposal.Statement.Length,
                conversationId);

            queue(t => AcceptAsync(
                app, client, agent, workspace, project, acceptance, t, queue, done));
        };
    }

    /// <summary>What the person reads before they accept.</summary>
    /// <param name="agent">Whose line it is.</param>
    /// <param name="live">What peers read today.</param>
    /// <param name="proposal">What the agent said.</param>
    /// <param name="neighbours">What the other agents in the project already claim.</param>
    /// <returns>The description.</returns>
    /// <remarks>
    /// <b>Attributed, and marked as the agent's own words</b> (FR-013). The sender and the time are
    /// both said, because a reader has to be able to tell
    /// a model's sentence from a person's — and a screen that showed the text alone would make the
    /// record of <c>proposed</c> versus <c>composed</c> a claim nobody could check.
    /// </remarks>
    internal static string Reviewed(
        AgentName agent,
        string? live,
        OwnershipProposal proposal,
        IReadOnlyList<(string Name, string Owns)>? neighbours = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var now = OwnershipStatement.Read(live) is { } already
            ? $"'{Safe.Text(already)}'"
            : "nothing -- this agent has no line yet";

        return $"""
                {Safe.Text(agent.Value)}

                Peers read {now} today.

                {Safe.Text(agent.Value)} proposed this, at {When(proposal)} local time. These are
                its words, not this screen's:

                {Safe.Lines(proposal.Statement)}

                Enter accepts it as it stands. Editing it first records it as yours rather than as
                the agent's. Either way it reaches peers when the agent next starts, because the
                line comes from its launch configuration.
                {Beside(neighbours)}
                """;
    }

    /// <summary>What the other agents already claim, for reading beside a proposal.</summary>
    /// <param name="neighbours">Their names and lines, or null when they were not read.</param>
    /// <returns>The block to draw, or an empty string when there is nothing to say.</returns>
    /// <remarks>
    /// <b>Shown and never judged</b> (FR-030, Principle II). An overlap is a real state of a deployment
    /// and it is the person's to settle or to leave -- a screen that flagged a clash, ranked the claims or
    /// refused the line would be arbitration arriving where nobody would look for it, and no agent has
    /// authority over another.
    /// </remarks>
    private static string Beside(IReadOnlyList<(string Name, string Owns)>? neighbours)
    {
        if (neighbours is null || neighbours.Count == 0)
        {
            return string.Empty;
        }

        var lines = neighbours
            .Take(Named)
            .Select(peer => $"  {Safe.Text(peer.Name)}: {Safe.Text(peer.Owns)}");

        var rest = neighbours.Count > Named
            ? $"{Environment.NewLine}  and {neighbours.Count - Named} more."
            : string.Empty;

        return Environment.NewLine
             + Environment.NewLine
             + "What the others already say they own:"
             + Environment.NewLine
             + string.Join(Environment.NewLine, lines)
             + rest;
    }

    /// <summary>Whose words an accepted line is, decided by comparing them.</summary>
    /// <param name="accepted">What the person accepted.</param>
    /// <param name="proposed">What the agent had written.</param>
    /// <returns>Which of the two routes produced it.</returns>
    /// <remarks>
    /// <para>
    /// <b>Ordinal, and deliberately strict about everything except the surrounding whitespace.</b>
    /// The field trims, so a trailing space is the dialog's doing rather than the person's — but a
    /// changed letter, a changed case or a changed dash is an edit, because it must be possible
    /// to say the words are the agent's, and words that were altered are not.
    /// </para>
    /// <para>
    /// A comparison rather than a flag, so that no path can record <c>proposed</c> for something
    /// somebody rewrote. A flag would be right every time it was set correctly.
    /// </para>
    /// </remarks>
    internal static OwnershipSource Chosen(string accepted, string proposed) =>
        string.Equals(
            (accepted ?? string.Empty).Trim(),
            (proposed ?? string.Empty).Trim(),
            StringComparison.Ordinal)
            ? OwnershipSource.Proposed
            : OwnershipSource.Edited;

    /// <summary>When a proposal was written, in the reader's own zone.</summary>
    /// <param name="proposal">The proposal.</param>
    /// <returns>The time, to the minute.</returns>
    /// <remarks>
    /// Local, and the caller says so in words, because every other time on this surface is local and
    /// an unmarked UTC beside them is the defect 0.27.0 records paying for on three screens at once.
    /// The culture is invariant so the spelling does not move with the machine.
    /// </remarks>
    private static string When(OwnershipProposal proposal) =>
        proposal.Message.CreatedUtc
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Writes the line where it takes effect, then records that a person accepted it.</summary>
    /// <remarks>
    /// <para>
    /// <b>The file first and the record second, and the order is chosen for which half is
    /// recoverable.</b> A file written with no record leaves the line live and its provenance
    /// unrecorded, which the next acceptance repairs; a record with no file would claim somebody
    /// accepted a line that peers will never read, and nothing repairs a wrong record.
    /// </para>
    /// <para>
    /// So a failed write stops before the broker is told anything — there is nothing to record yet —
    /// and a failed record after a successful write says both halves, because the line really is on
    /// its way to peers and only the provenance is missing.
    /// </para>
    /// </remarks>
    internal static async Task AcceptAsync(
        IApplication app,
        BrokerClient client,
        AgentName agent,
        string? workspace,
        string? project,
        OwnershipAcceptance acceptance,
        CancellationToken cancellationToken,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var wrong = await OwnershipLine
            .WriteAsync(workspace, project, agent, acceptance.Statement, cancellationToken)
            .ConfigureAwait(false);

        if (wrong is not null)
        {
            await done($"Not recorded: {Safe.Text(wrong)}").ConfigureAwait(false);

            return;
        }

        var outcome = await BrokerDirectory.ReadAsync(
            async () => await client.RecordOwnershipAsync(
                agent, acceptance, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!outcome.Reachable)
        {
            await done(
                $"Written to {Safe.Text(OwnershipLine.PathIn(workspace!))}, and it takes effect when "
                + $"the agent next starts. The broker could not be told a person accepted it: "
                + Safe.Text(outcome.Problem!)).ConfigureAwait(false);

            return;
        }

        await done(Said(outcome.Value!)).ConfigureAwait(false);

        // **Offered only when it would change something.** A line that already agrees with what peers
        // read needs no restart, and offering one anyway would spend SPEC.md §12's cost -- the process
        // holds everything the agent has learned -- to achieve nothing.
        if (outcome.Value!.Recorded && outcome.Value.Pending)
        {
            Restart(app, client, agent, queue, done);
        }
    }

    /// <summary>Offers the restart that makes an accepted line the one peers read (FR-019).</summary>
    /// <param name="app">The application the dialog opens in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="agent">Whose line it is.</param>
    /// <param name="queue">Runs the start, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <remarks>
    /// <para>
    /// <b>Offered rather than done.</b> SPEC.md §12 says what stopping an agent costs: the process is
    /// what held everything it had learned. Accepting a sentence about scope is not a reason to spend
    /// that without asking, so the safe answer is first and leaving it is a complete outcome rather
    /// than a failure -- the roster carries the pending mark until the agent comes back on its own.
    /// </para>
    /// <para>
    /// <b>It starts rather than stops.</b> <c>Operators.Start</c> is what this surface has, and 006
    /// established it launches a catalogued role without a registry lookup. An agent already running
    /// is asked for a turn instead, which is why the report says what was asked for rather than
    /// claiming the line is now live.
    /// </para>
    /// </remarks>
    private static void Restart(
        IApplication app,
        BrokerClient client,
        AgentName agent,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var asking = Prompting.Choose(RestartTitle, Restarted(agent), [LeaveIt, RestartIt]);
        var open = Prompting.Open(app, asking);

        asking.Done += (_, chosen) =>
        {
            Prompting.Close(app, open);

            if (!string.Equals(chosen, RestartIt, StringComparison.Ordinal))
            {
                _ = done(NotRestarted);

                return;
            }

            // **On the surface's own work queue and never a bare Task.Run.** `Shell.Settled` is what
            // makes every driven test deterministic, and 005's gate found work escaping that chain --
            // a guarantee that holds only because of where its callers happen to sit is one nothing
            // enforces. This handler runs on the main loop, so queueing from it is the ordinary route.
            queue(async t =>
            {
                var read = await BrokerDirectory.ReadAsync(
                    async () => await client
                        .StartAsync(agent, "a person accepted what it owns", t)
                        .ConfigureAwait(false))
                    .ConfigureAwait(false);

                await done(read.Reachable
                    ? Restarting
                    : $"Not restarted: {Safe.Text(read.Problem!)}").ConfigureAwait(false);
            });
        };
    }

    /// <summary>What the person reads before the restart.</summary>
    /// <param name="agent">Whose line it is.</param>
    /// <returns>The description.</returns>
    internal static string Restarted(AgentName agent) =>
        $"""
         {Safe.Text(agent.Value)}

         The line is recorded, and peers do not read it yet: the registry is filled from an agent's
         launch configuration when it attaches, so it takes effect at its next start.

         Starting it now makes that happen immediately. It costs whatever that session had in
         progress -- an agent's process is what holds everything it has learned -- so leaving it is
         an ordinary answer, and the roster marks the line as accepted until the two agree.
         """;

    /// <summary>What to put on the message line.</summary>
    /// <param name="record">What came back.</param>
    /// <returns>One line, safe to draw.</returns>
    /// <remarks>
    /// Internal so a test can hold the wording without driving a broker, and because
    /// <c>DeadSurfaceTests</c> reports a constant named only in its own file.
    /// </remarks>
    internal static string Said(OwnershipRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!record.Recorded)
        {
            return $"Not recorded: {Safe.Text(record.Problem ?? "the broker gave no reason.")}";
        }

        if (!record.Pending)
        {
            return "Recorded, and it is what peers already read.";
        }

        var live = record.LiveStatement is null
            ? "no line for this agent"
            : $"'{Safe.Text(record.LiveStatement)}'";

        return $"Recorded. Peers read {live} until it next starts, because the line comes from its "
             + "launch configuration.";
    }
}
