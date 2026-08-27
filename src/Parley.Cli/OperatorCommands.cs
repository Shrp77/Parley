using System.Globalization;
using System.Text;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Cli;

/// <summary>
/// The operator operations of SPEC.md §10.2, rendered for a terminal.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <see cref="Program"/> so the behaviour can be tested without a process. What
/// is left in the entry point is argument shape and exit codes; what is here is what each
/// command does and what it prints, which is the part worth pinning.
/// </para>
/// <para>
/// Every command writes to a supplied writer rather than to the console for the same reason.
/// </para>
/// </remarks>
public sealed class OperatorCommands
{
    private readonly Wire.Operators.OperatorsClient _console;
    private readonly TextWriter _output;

    /// <summary>Creates the commands over an open database.</summary>
    /// <param name="console">The operator console to drive.</param>
    /// <param name="output">Where to write.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OperatorCommands(Wire.Operators.OperatorsClient console, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(output);

        _console = console;
        _output = output;
    }

    /// <summary>Lists every registered agent, whole.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Zero.</returns>
    /// <remarks>
    /// Shows the workspace, which the agent-facing listing deliberately does not (§10.1 rule 3).
    /// An operator is looking at their own deployment; a peer is not.
    /// </remarks>
    public async Task<int> RegistryAsync(CancellationToken cancellationToken)
    {
        var reply = await _console.DescribeRegistryAsync(
            new Wire.Empty(), cancellationToken: cancellationToken);

        var agents = reply.Rows;

        if (agents.Count == 0)
        {
            await _output.WriteLineAsync(
                "No agents registered. One appears the first time its server starts.")
                .ConfigureAwait(false);

            return 0;
        }

        foreach (var agent in agents.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                // Two columns rather than one, because they answer different questions and an
                // operator needs both. Status is a judgement somebody recorded -- active, or
                // retired. Presence is what the connections say right now, and nothing about it
                // gates routing: an agent that is `startable` is simply one nobody has open,
                // which is where most agents are most of the time.
                $"{agent.Name,-20} {Lower(agent.Status),-9} {Lower(agent.Presence),-10} " +
                $"{Lower(agent.Tier),-9} {agent.Adapter,-12} " +
                $"{agent.Workspace}")).ConfigureAwait(false);

            // The statement its peers route by, on its own line because it is prose rather than
            // a column. Its absence is printed rather than skipped: the field is optional and
            // the feature is inert without it, so this listing is where the person who can set
            // it finds out that nobody has. That is the whole of the nudge — a note on every
            // start-up would land in whichever log the client keeps for a server that is
            // misbehaving, which nobody reads while nothing is wrong.
            await _output.WriteLineAsync(
                $"  owns: {(agent.Owns is { Length: > 0 } owns ? owns : $"(unset — set {DeploymentEnvironment.OwnsVariable} where that agent's server is launched)")}")
                .ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Reports what the message size cap refused.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Zero.</returns>
    /// <remarks>
    /// <para>
    /// The evidence for the one number in this protocol that has never had any. Rule 8.5 fixes a
    /// message size and SPEC.md §17 admits the clarify cap is a guess and asks for instrumentation
    /// before tuning; neither was ever measured. Raising the cap from two thousand to four
    /// thousand was judgement again, and this is what makes the next change something else.
    /// </para>
    /// <para>
    /// Prints the mean beside the worst, because they answer different questions. Forty refusals
    /// averaging just over the cap is a cap set slightly too low; one refusal at forty thousand
    /// characters is somebody trying to send a file, and only the first is an argument for
    /// changing the number.
    /// </para>
    /// </remarks>
    public async Task<int> RefusalsAsync(CancellationToken cancellationToken)
    {
        var refusalsReply = await _console.DescribeRefusalsAsync(
            new Wire.Empty(), cancellationToken: cancellationToken);

        var refusals = refusalsReply.Refusals;

        if (refusals.Count == 0)
        {
            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Nothing has been refused for length. The cap is currently " +
                $"{MessageLimits.MaxPayloadCharacters} characters; there is no evidence here " +
                $"for changing it.")).ConfigureAwait(false);

            return 0;
        }

        await _output.WriteLineAsync(
            "agent                type      count  worst   mean   cap  last").ConfigureAwait(false);

        foreach (var refusal in refusals)
        {
            var mean = refusal.Total / refusal.Refusals;

            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{refusal.Agent,-20} {Lower(refusal.Type),-9} {refusal.Refusals,5}  " +
                $"{refusal.Worst,5}  {mean,5} {refusal.Cap,5}  {Readable(refusal.LastUtc)}"))
                .ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// Reports where the ownership map is being argued about, per SPEC.md §10.2.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Zero. The report is the output.</returns>
    /// <remarks>
    /// <para>
    /// Two halves from two tables, and the report says which is which because they have different
    /// reach. The rate comes from <c>exchanges</c>, which retention never touches, so it covers
    /// the whole life of the deployment. The targets come from the referral record, which exists
    /// because the names used to live in a message payload that §9.4 deletes.
    /// </para>
    /// <para>
    /// Written so a high number reads as a question rather than a verdict. A pair referring
    /// constantly is not an agent behaving badly; it is two repositories that disagree about a
    /// boundary, which is a thing people fix and not a thing an operator should read as a fault.
    /// </para>
    /// </remarks>
    public async Task<int> ReferralsAsync(CancellationToken cancellationToken)
    {
        var report = await _console.DescribeReferralsAsync(
            new Wire.Empty(), cancellationToken: cancellationToken);

        if (IsEmpty(report))
        {
            await _output.WriteLineAsync(
                "Nothing has been referred. Either every question has found its owner first " +
                "time, or too little has been asked to tell.").ConfigureAwait(false);

            return 0;
        }

        await _output.WriteLineAsync(
            "asker                answerer             closed  out of scope   rate")
            .ConfigureAwait(false);

        foreach (var pair in report.Pairs)
        {
            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{pair.Asker,-20} {pair.Answerer,-20} {pair.Closed,6}  {pair.OutOfScope,12}  " +
                $"{Rate(pair),5:0.0%}")).ConfigureAwait(false);
        }

        if (report.Targets.Count > 0)
        {
            await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
            await _output.WriteLineAsync(
                "and where they were sent instead").ConfigureAwait(false);

            foreach (var target in report.Targets)
            {
                await _output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{target.Asker,-20} {target.Answerer,-20} -> {target.RedirectedTo,-20} " +
                    $"{target.Referrals,5}  {Readable(target.LastUtc)}")).ConfigureAwait(false);
            }
        }

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);

        // The reading. §15 calls this the signal that the ownership map is wrong somewhere, and
        // an operator looking at a table of counts should not have to reconstruct that.
        await _output.WriteLineAsync(
            "A high rate is a boundary two repositories disagree about, not an agent behaving " +
            "badly. Fix it where ownership is written down, not here.").ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// Reports what Notify has carried, per SPEC.md §10.2.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Zero. The report is the output.</returns>
    /// <remarks>
    /// §17 question 1 records that nothing bounds Notify and that the answer is to measure rather
    /// than pick a limit. This prints the two numbers that decide it — how many and how big — and
    /// the per-recipient totals a survey reads, because asking an agent whether notifications are
    /// worth having is only meaningful once it has had enough of them to judge.
    /// </remarks>
    public async Task<int> NotificationsAsync(CancellationToken cancellationToken)
    {
        var report = await _console.DescribeNotificationsAsync(new Wire.Empty(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (IsEmpty(report))
        {
            await _output.WriteLineAsync(
                "No notifications have been sent. Nothing bounds them by design, so this is the " +
                "evidence for whether anything should — and there is none yet.")
                .ConfigureAwait(false);

            return 0;
        }

        await _output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{Sent(report)} notifications, mean {Mean(report)} characters. The cap on a single " +
            $"message is {MessageLimits.MaxPayloadCharacters}.")).ConfigureAwait(false);

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await _output.WriteLineAsync(
            "from                 to                    sent    mean  largest  first        last")
            .ConfigureAwait(false);

        foreach (var flow in report.Flows)
        {
            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{flow.From,-20} {flow.To,-20} {flow.Sent,6}  {Mean(flow),6} {flow.Largest,8}  " +
                $"{Day(flow.FirstUtc)}  {Day(flow.LastUtc)}")).ConfigureAwait(false);
        }

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await _output.WriteLineAsync("received in total").ConfigureAwait(false);

        foreach (var (agent, received) in Received(report))
        {
            // The thresholds are where somebody goes and asks that agent whether any of it was
            // worth having. Marked rather than left to be counted off a column, because the whole
            // point of the survey is that it happens at all.
            var due = received >= 10
                ? string.Create(CultureInfo.InvariantCulture, $"  <- ask it, past {received / 10 * 10}")
                : string.Empty;

            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{agent,-20} {received,6}{due}")).ConfigureAwait(false);
        }

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await _output.WriteLineAsync(
            "Nothing refuses a notification, deliberately. What to do with these numbers is in " +
            "SPEC.md section 17, question 1.").ConfigureAwait(false);

        return 0;
    }

    /// <summary>Renders a stored timestamp as a date, for a column that spans days.</summary>
    /// <param name="stored">The value as the database holds it.</param>
    /// <returns>The date, or the raw value if it cannot be read as one.</returns>
    private static string Day(string stored) =>
        DateTimeOffset.TryParse(
            stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
            ? moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : stored;

    /// <summary>Renders a stored timestamp the way the other reports do.</summary>
    /// <param name="stored">The value as the database holds it.</param>
    /// <returns>The same instant, or the raw value if it cannot be read as one.</returns>
    /// <remarks>
    /// Storage keeps full round-trip precision because deduplication buckets are derived from it;
    /// seven decimal places in a column an operator is scanning for a date is noise. Falls back to
    /// the stored text rather than throwing: a report is what somebody runs when something has
    /// already gone wrong, and refusing to print because one row is odd is the worst moment to
    /// discover that.
    /// </remarks>
    private static string Readable(string stored) =>
        DateTimeOffset.TryParse(
            stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
            ? moment.ToString("u", CultureInfo.InvariantCulture)
            : stored;

    /// <summary>
    /// Reports what clarification cost, per SPEC.md §10.2.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Zero. The report is the output.</returns>
    /// <remarks>
    /// <para>
    /// Written to be read by somebody deciding whether the cap of §5.2 should exist, so it says
    /// what the numbers mean rather than only what they are. A distribution printed without the
    /// caps that shaped it is a censored measurement presented as a natural one, which is the
    /// specific mistake §17 question 4 exists to avoid.
    /// </para>
    /// <para>
    /// The empty case says what is missing rather than printing an empty table, for the same
    /// reason <see cref="RefusalsAsync"/> does: the person most likely to be looking is the one
    /// who wants the number changed, and "nothing has happened yet" is an answer to that.
    /// </para>
    /// </remarks>
    public async Task<int> ClarificationsAsync(CancellationToken cancellationToken)
    {
        var report = await _console.DescribeClarificationsAsync(new Wire.Empty(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (Exchanges(report) == 0)
        {
            await _output.WriteLineAsync(
                "No exchanges yet, so nothing has clarified. There is no evidence here for or " +
                "against the clarify cap.").ConfigureAwait(false);

            return 0;
        }

        await _output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{Exchanges(report)} exchanges. Caps in force: {report.LowestCapInForce} to " +
            $"{report.HighestCapInForce}.")).ConfigureAwait(false);

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await _output.WriteLineAsync("depth  exchanges  share").ConfigureAwait(false);

        foreach (var bucket in report.Distribution)
        {
            var share = 100.0 * bucket.Exchanges / Exchanges(report);

            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{bucket.Depth,5}  {bucket.Exchanges,9}  {share,5:0.0}%")).ConfigureAwait(false);
        }

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);

        await _output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Reached the cap:                             {report.ExchangesAtCap}")).ConfigureAwait(false);

        await _output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Answers resolving sub-questions differently: " +
            $"{report.AnswersResolvingSubQuestionsDifferently} of {report.Answers}"))
            .ConfigureAwait(false);

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);

        // The reading, not just the numbers. Somebody who came here to pick a cap should not
        // have to reconstruct from a histogram that a pile at the top means the measurement was
        // truncated rather than that the truth was found.
        if (report.ExchangesAtCap > 0 && DeepestReached(report) >= report.HighestCapInForce)
        {
            await _output.WriteLineAsync(
                "The distribution is censored: exchanges are stopping at the cap, so the depth " +
                "they would have reached is not in this data. Raise the cap and measure again " +
                "before choosing a number.").ConfigureAwait(false);
        }
        else if (DeepestReached(report) == 0)
        {
            await _output.WriteLineAsync(
                "Nothing has clarified even once. The cap is not what is keeping depth low, so " +
                "there is nothing here to tune.").ConfigureAwait(false);
        }
        else
        {
            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Nothing reached the cap and the deepest was {DeepestReached(report)}, so this " +
                $"distribution is the real one rather than a truncated one."))
                .ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Withdraws an agent's registration.</summary>
    /// <param name="agent">The agent to retire.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Zero if the registry changed, one if it did not.</returns>
    /// <remarks>
    /// Reports what already-retired means rather than treating it as failure, because running
    /// the command twice is not a mistake worth an error — but it exits non-zero so a script
    /// that expected to change something can tell.
    /// </remarks>
    public async Task<int> RetireAsync(AgentName agent, CancellationToken cancellationToken)
    {
        var retired = await _console.RetireAsync(
            new Wire.AgentRequest { Agent = agent.Value }, cancellationToken: cancellationToken);

        if (!retired.Changed)
        {
            await _output.WriteLineAsync(
                $"'{agent.Value}' is not registered, or was already retired.").ConfigureAwait(false);

            return 1;
        }

        await _output.WriteLineAsync(
            $"'{agent.Value}' is retired. It will not be routed to or listed, and starting its " +
            "server will be refused. What it already owed is not resolved here: those exchanges " +
            "run out of time on the ordinary schedule and each asker is told nobody answered.")
            .ConfigureAwait(false);

        return 0;
    }

    /// <summary>Deletes an agent and everything it was party to.</summary>
    /// <param name="agent">The agent to purge.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Zero if the agent was deleted, one if there was no such agent.</returns>
    /// <remarks>
    /// The toll is reported per table rather than as a total, because the total is the one
    /// number that tells an operator nothing: "forty-one rows" could be a dead registration's
    /// leftovers or two months of a working conversation. Printed after the deletion rather than
    /// before, since nothing here is a confirmation prompt — by the time this runs the decision
    /// has been made, and what an operator needs is an account of what is now missing.
    /// </remarks>
    public async Task<int> PurgeAsync(AgentName agent, CancellationToken cancellationToken)
    {
        var outcome = await _console.PurgeAsync(
            new Wire.AgentRequest { Agent = agent.Value }, cancellationToken: cancellationToken);

        switch (outcome.Verdict)
        {
            case Wire.PurgeVerdict.Gone:
                if (outcome.Destroyed.Count == 0)
                {
                    await _output.WriteLineAsync(
                        $"'{agent.Value}' is purged. It had never traded, so nothing went with " +
                        "it, and the name is free to register again.").ConfigureAwait(false);

                    return 0;
                }

                await _output.WriteLineAsync(
                    $"'{agent.Value}' is purged, and so is everything it was party to:")
                    .ConfigureAwait(false);

                var width = outcome.Destroyed.Max(toll => toll.Table.Length) + 1;

                foreach (var toll in outcome.Destroyed)
                {
                    await _output.WriteLineAsync(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {toll.Table.PadRight(width)}  {toll.Rows}")).ConfigureAwait(false);
                }

                // Said afterwards rather than before, because it is the part an operator needs
                // once it is too late to matter: the counterparties were not asked and their side
                // of those conversations is gone too. Retirement is the command that keeps it.
                await _output.WriteLineAsync(
                    "This cannot be undone. Any peer that spoke with it has lost that " +
                    "conversation as well — retire an agent instead when the record matters.")
                    .ConfigureAwait(false);

                return 0;

            default:
                await _output.WriteLineAsync(
                    $"'{agent.Value}' is not registered.").ConfigureAwait(false);

                return 1;
        }
    }

    /// <summary>Withdraws a retirement.</summary>
    /// <param name="agent">The agent to reinstate.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Zero if the registry changed, one if it did not.</returns>
    public async Task<int> ReinstateAsync(AgentName agent, CancellationToken cancellationToken)
    {
        var reinstated = await _console.ReinstateAsync(
            new Wire.AgentRequest { Agent = agent.Value }, cancellationToken: cancellationToken);

        if (!reinstated.Changed)
        {
            await _output.WriteLineAsync(
                $"'{agent.Value}' is not registered, or was not retired.").ConfigureAwait(false);

            return 1;
        }

        await _output.WriteLineAsync(
            $"'{agent.Value}' is reinstated. It is routable again once its server attaches.")
            .ConfigureAwait(false);

        return 0;
    }

    /// <summary>Emits every message in a conversation.</summary>
    /// <param name="conversationId">The discussion to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Zero if anything was found, one if not.</returns>
    /// <remarks>
    /// Payloads are printed as stored, not wrapped in the envelope of §13. An envelope marks a
    /// peer's words as data inside another agent's context, and an operator reading their own
    /// log is not being asked to obey it — wrapping would also make the export unusable for the
    /// purpose §10.2 gives it, which is comparing how model pairings converge.
    /// </remarks>
    public async Task<int> ExportAsync(string conversationId, CancellationToken cancellationToken)
    {
        var exported = await _console.ExportAsync(
            new Wire.ExportRequest { ConversationId = conversationId },
            cancellationToken: cancellationToken);

        var messages = exported.Messages;

        await Task.CompletedTask
            .ConfigureAwait(false);

        if (messages.Count == 0)
        {
            await _output.WriteLineAsync(
                $"No messages in '{conversationId}'. Conversations exist only once an agent has " +
                "named one on a question or an event.").ConfigureAwait(false);

            return 1;
        }

        foreach (var message in messages)
        {
            var text = new StringBuilder()
                .Append(message.CreatedUtc)
                .Append("  ")
                // Nine, not eight: "question" is exactly eight characters, so padding to its
                // own length runs the type into the sender. Found by reading the output.
                .Append(Lower(message.Type).PadRight(9))
                .Append(message.From)
                .Append(" -> ")
                .Append(message.To)
                .Append("  (")
                .Append(message.ResponderModel)
                .Append(')');

            await _output.WriteLineAsync(text.ToString()).ConfigureAwait(false);
            await _output.WriteLineAsync("    " + message.PayloadJson).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Closes a conversation over its participants' votes.</summary>
    /// <param name="conversationId">The discussion to close.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Zero if it closed, one if it did not.</returns>
    /// <remarks>
    /// Named <c>force-conclude</c> rather than <c>conclude</c>, following §10.2. It exists
    /// because a decline blocks closure indefinitely and the agent holding it may simply be
    /// wrong — a deliberate asymmetry rather than a hole in principle 2.6: no agent may conclude
    /// over another's objection, and the person running the broker is not an agent.
    /// </remarks>
    public async Task<int> ForceConcludeAsync(
        string conversationId, CancellationToken cancellationToken)
    {
        var concluded = await _console.ForceConcludeAsync(
            new Wire.ForceConcludeRequest { ConversationId = conversationId },
            cancellationToken: cancellationToken);

        if (!concluded.Changed)
        {
            await _output.WriteLineAsync(
                $"'{conversationId}' is not open, or does not exist.").ConfigureAwait(false);

            return 1;
        }

        await _output.WriteLineAsync(
            $"'{conversationId}' is closed. A new question naming it will be refused; the same " +
            "question under a new identifier will not.").ConfigureAwait(false);

        return 0;
    }

    /// <summary>Applies the retention policy of SPEC.md §9.4 now.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>Zero.</returns>
    /// <remarks>
    /// <para>
    /// The policy is the broker's, and it is no longer printed here — which is a loss worth
    /// stating rather than glossing. This used to read the retention variables from the
    /// operator's own shell and print what it applied, because a shell is not the environment an
    /// MCP client launched the servers with and a command that deletes text on a schedule must
    /// say which schedule it used. That reasoning was right and its premise is gone: there is one
    /// process holding the store now, it was started with one policy, and this command cannot
    /// apply a different one however the shell is configured. The remaining gap is that an
    /// operator on another machine cannot see the numbers from here, and that is a reason to add
    /// them to the reply rather than to read a local variable that no longer decides anything.
    /// </para>
    /// <para>
    /// Zero even when nothing was removed, unlike <see cref="RetireAsync"/> and
    /// <see cref="ReinstateAsync"/>. Those exit non-zero when the registry did not change
    /// because a script that expected a change can then tell; here "nothing was old enough" is
    /// the ordinary steady state of a healthy deployment, and a cron job that failed most nights
    /// would train whoever reads it to ignore the one night it meant something.
    /// </para>
    /// </remarks>
    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync(
            $"Retention is the broker's, set from {RetentionPolicyEnvironment.IdleVariable} and " +
            $"{RetentionPolicyEnvironment.MaximumAgeVariable} in the environment `parley serve` " +
            "was started with.").ConfigureAwait(false);

        var result = await _console.PruneAsync(
            new Wire.Empty(), cancellationToken: cancellationToken);

        var removed = result.Removed.ToString(CultureInfo.InvariantCulture);

        await _output.WriteLineAsync($"Removed {removed} message(s).").ConfigureAwait(false);

        if (result.Capped)
        {
            // Only ever seen on a first prune against a log that grew without a policy. Saying
            // nothing would leave an operator believing the log was now inside its retention,
            // which is the one thing this command exists to tell them.
            await _output.WriteLineAsync(
                "That is as much as one pass removes, so there is more. Run it again, or leave " +
                "it to the sweep — a backlog drains over several passes rather than holding the " +
                "database for one long one.").ConfigureAwait(false);
        }

        if (result.HeldByFloor > 0)
        {
            // Said only when it is true, because the explanation is long and an operator who
            // sees it every time stops reading it. A number that stays high across runs is the
            // signal: exchanges are sitting open, or a peer has stopped collecting its inbox.
            var held = result.HeldByFloor.ToString(CultureInfo.InvariantCulture);

            await _output.WriteLineAsync(
                $"{held} more were old enough and were kept: still undelivered, on an exchange " +
                "that has not closed, or inside the deduplication window. That floor is not " +
                "configurable — it is what keeps tightening retention from breaking delivery " +
                "or deduplication as a side effect.").ConfigureAwait(false);
        }

        if (result.Contended > 0)
        {
            // Reported apart from the floor because it means the opposite thing. The floor
            // holding traffic is worth looking into; the broker's own sweep having got there
            // first is the system working — it applies the same policy on its own schedule, and
            // this command is not the only thing applying it.
            var contended = result.Contended.ToString(CultureInfo.InvariantCulture);

            await _output.WriteLineAsync(
                $"{contended} were already gone: the broker's own sweep took them between this " +
                "command deciding and doing. Nothing is wrong — it applies the same policy on " +
                "its own schedule.").ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Renders a duration as whole hours.</summary>
    /// <param name="value">The duration.</param>
    /// <returns>Text an operator can compare against what they configured.</returns>
    /// <remarks>
    /// Hours because that is the unit the variables are stated in, and a command that echoes a
    /// setting back in different units than it was written in is one more thing to convert
    /// while deciding whether it is right.
    /// </remarks>
    private static string Hours(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.TotalHours:0.##} hours");

    /// <summary>Whether a referral report found nothing.</summary>
    /// <param name="report">The report.</param>
    /// <returns><see langword="true"/> if there is nothing to show.</returns>
    private static bool IsEmpty(Wire.ReferralsReply report) =>
        report.Pairs.Count == 0 && report.Targets.Count == 0;

    /// <summary>Whether a notification report found nothing.</summary>
    /// <param name="report">The report.</param>
    /// <returns><see langword="true"/> if there is nothing to show.</returns>
    private static bool IsEmpty(Wire.NotificationsReply report) => report.Flows.Count == 0;

    /// <summary>Every notification sent, across all flows.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The total.</returns>
    private static long Sent(Wire.NotificationsReply report) => report.Flows.Sum(f => f.Sent);

    /// <summary>The mean size of a notification across all flows.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The mean, in characters.</returns>
    /// <remarks>
    /// Weighted by the messages rather than averaged across the flows, so one pair sending a
    /// thousand short notices is not balanced out by a pair that sent one long one.
    /// </remarks>
    private static long Mean(Wire.NotificationsReply report)
    {
        var sent = Sent(report);

        return sent == 0 ? 0 : report.Flows.Sum(f => f.Characters) / sent;
    }

    /// <summary>How much each agent has received, most first.</summary>
    /// <param name="report">The report.</param>
    /// <returns>One entry per recipient.</returns>
    /// <remarks>
    /// The half of SPEC.md §17 question 1 that a counter can answer: how much an agent has been
    /// sent. Whether any of it was worth reading only that agent knows, which is why the answer
    /// is asked for rather than measured.
    /// </remarks>
    private static IReadOnlyList<(string Agent, long Received)> Received(
        Wire.NotificationsReply report) =>
        [.. report.Flows
            .GroupBy(f => f.To, StringComparer.Ordinal)
            .Select(g => (Agent: g.Key, Received: g.Sum(f => f.Sent)))
            .OrderByDescending(x => x.Received)
            .ThenBy(x => x.Agent, StringComparer.Ordinal)];

    /// <summary>How many exchanges the clarify distribution covers.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The total.</returns>
    private static long Exchanges(Wire.ClarificationsReply report) =>
        report.Distribution.Sum(b => b.Exchanges);

    /// <summary>The deepest clarification any exchange reached.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The depth, or zero if none did.</returns>
    private static int DeepestReached(Wire.ClarificationsReply report) =>
        report.Distribution.Count == 0 ? 0 : report.Distribution.Max(b => b.Depth);

    /// <summary>The share of a pair's closed exchanges the answerer declined as not its own.</summary>
    /// <param name="pair">The pair.</param>
    /// <returns>The rate, between zero and one.</returns>
    /// <remarks>
    /// Computed here because the wire carries the two counts and not the quotient. A derived
    /// field on the wire would be a second place the arithmetic could be done differently, and
    /// this is the one number in SPEC.md §15 that an operator acts on.
    /// </remarks>
    private static double Rate(Wire.ReferralPair pair) =>
        pair.Closed == 0 ? 0 : (double)pair.OutOfScope / pair.Closed;

    /// <summary>The mean size of a notification on one flow.</summary>
    /// <param name="flow">The flow.</param>
    /// <returns>The mean, in characters.</returns>
    private static long Mean(Wire.NotifyFlow flow) =>
        flow.Sent == 0 ? 0 : flow.Characters / flow.Sent;

    /// <summary>Renders an enum member the way the registry stores it.</summary>
    /// <param name="value">The member.</param>
    /// <returns>Its lowercase name.</returns>
    private static string Lower(object value) =>
        value.ToString()!.ToLowerInvariant();
}
