using System.Globalization;
using System.Text;
using Parley.Broker;
using Parley.Core;
using Parley.Store;

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
    private readonly OperatorConsole _console;
    private readonly TextWriter _output;

    /// <summary>Creates the commands over an open database.</summary>
    /// <param name="console">The operator console to drive.</param>
    /// <param name="output">Where to write.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OperatorCommands(OperatorConsole console, TextWriter output)
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
        var agents = await _console.DescribeRegistryAsync(cancellationToken).ConfigureAwait(false);

        if (agents.Count == 0)
        {
            await _output.WriteLineAsync(
                "No agents registered. One appears the first time its server starts.")
                .ConfigureAwait(false);

            return 0;
        }

        foreach (var agent in agents.OrderBy(a => a.Name.Value, StringComparer.Ordinal))
        {
            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{agent.Name.Value,-20} {Lower(agent.Status),-12} {Lower(agent.Tier),-9} " +
                $"{agent.Adapter,-12} {agent.Workspace ?? ""}")).ConfigureAwait(false);

            // The statement its peers route by, on its own line because it is prose rather than
            // a column. Its absence is printed rather than skipped: the field is optional and
            // the feature is inert without it, so this listing is where the person who can set
            // it finds out that nobody has. That is the whole of the nudge — a note on every
            // start-up would land in whichever log the client keeps for a server that is
            // misbehaving, which nobody reads while nothing is wrong.
            await _output.WriteLineAsync(
                $"  owns: {agent.Owns ?? $"(unset — set {DeploymentEnvironment.OwnsVariable} where that agent's server is launched)"}")
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
        var refusals = await _console.DescribeRefusalsAsync(cancellationToken).ConfigureAwait(false);

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
                $"{refusal.Agent.Value,-20} {Lower(refusal.Type),-9} {refusal.Refusals,5}  " +
                $"{refusal.Worst,5}  {mean,5} {refusal.Cap,5}  {refusal.LastUtc:u}"))
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
        var report = await _console.DescribeReferralsAsync(cancellationToken).ConfigureAwait(false);

        if (report.IsEmpty)
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
                $"{pair.Rate,5:0.0%}")).ConfigureAwait(false);
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
        var report = await _console.DescribeNotificationsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (report.IsEmpty)
        {
            await _output.WriteLineAsync(
                "No notifications have been sent. Nothing bounds them by design, so this is the " +
                "evidence for whether anything should — and there is none yet.")
                .ConfigureAwait(false);

            return 0;
        }

        await _output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{report.Sent} notifications, mean {report.Mean} characters. The cap on a single " +
            $"message is {MessageLimits.MaxPayloadCharacters}.")).ConfigureAwait(false);

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await _output.WriteLineAsync(
            "from                 to                    sent    mean  largest  first        last")
            .ConfigureAwait(false);

        foreach (var flow in report.Flows)
        {
            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{flow.From,-20} {flow.To,-20} {flow.Sent,6}  {flow.Mean,6} {flow.Largest,8}  " +
                $"{Day(flow.FirstUtc)}  {Day(flow.LastUtc)}")).ConfigureAwait(false);
        }

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await _output.WriteLineAsync("received in total").ConfigureAwait(false);

        foreach (var (agent, received) in report.Received)
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
        var report = await _console.DescribeClarificationsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (report.Exchanges == 0)
        {
            await _output.WriteLineAsync(
                "No exchanges yet, so nothing has clarified. There is no evidence here for or " +
                "against the clarify cap.").ConfigureAwait(false);

            return 0;
        }

        await _output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{report.Exchanges} exchanges. Caps in force: {report.LowestCapInForce} to " +
            $"{report.HighestCapInForce}.")).ConfigureAwait(false);

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await _output.WriteLineAsync("depth  exchanges  share").ConfigureAwait(false);

        foreach (var bucket in report.Distribution)
        {
            var share = 100.0 * bucket.Exchanges / report.Exchanges;

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
        if (report.ExchangesAtCap > 0 && report.DeepestReached >= report.HighestCapInForce)
        {
            await _output.WriteLineAsync(
                "The distribution is censored: exchanges are stopping at the cap, so the depth " +
                "they would have reached is not in this data. Raise the cap and measure again " +
                "before choosing a number.").ConfigureAwait(false);
        }
        else if (report.DeepestReached == 0)
        {
            await _output.WriteLineAsync(
                "Nothing has clarified even once. The cap is not what is keeping depth low, so " +
                "there is nothing here to tune.").ConfigureAwait(false);
        }
        else
        {
            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Nothing reached the cap and the deepest was {report.DeepestReached}, so this " +
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
        if (!await _console.RetireAsync(agent, cancellationToken).ConfigureAwait(false))
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
        var outcome = await _console.PurgeAsync(agent, cancellationToken).ConfigureAwait(false);

        switch (outcome.Verdict)
        {
            case PurgeVerdict.Gone:
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
        if (!await _console.ReinstateAsync(agent, cancellationToken).ConfigureAwait(false))
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
        var messages = await _console.ExportAsync(conversationId, cancellationToken)
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
                .Append(message.CreatedUtc.ToString("O", CultureInfo.InvariantCulture))
                .Append("  ")
                // Nine, not eight: "question" is exactly eight characters, so padding to its
                // own length runs the type into the sender. Found by reading the output.
                .Append(MessageIdentity.FormatType(message.Type).PadRight(9))
                .Append(message.From.Value)
                .Append(" -> ")
                .Append(message.To.Value)
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
    /// <param name="nowUtc">The instant to record as the close.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Zero if it closed, one if it did not.</returns>
    /// <remarks>
    /// Named <c>force-conclude</c> rather than <c>conclude</c>, following §10.2. It exists
    /// because a decline blocks closure indefinitely and the agent holding it may simply be
    /// wrong — a deliberate asymmetry rather than a hole in principle 2.6: no agent may conclude
    /// over another's objection, and the person running the broker is not an agent.
    /// </remarks>
    public async Task<int> ForceConcludeAsync(
        string conversationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (!await _console.ForceConcludeAsync(conversationId, nowUtc, cancellationToken)
                .ConfigureAwait(false))
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
    /// <param name="policy">The policy to apply.</param>
    /// <param name="nowUtc">The instant to measure ages against.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>Zero.</returns>
    /// <remarks>
    /// <para>
    /// Prints the policy before the counts, and that is the load-bearing part. The numbers come
    /// from the environment, and an operator's shell is not the environment their MCP client
    /// launched the servers with — so the command that deletes text on a configured schedule
    /// must say which schedule it used. Reporting only what went would let a default policy
    /// remove a year of log from a deployment configured to keep one, silently.
    /// </para>
    /// <para>
    /// Zero even when nothing was removed, unlike <see cref="RetireAsync"/> and
    /// <see cref="ReinstateAsync"/>. Those exit non-zero when the registry did not change
    /// because a script that expected a change can then tell; here "nothing was old enough" is
    /// the ordinary steady state of a healthy deployment, and a cron job that failed most nights
    /// would train whoever reads it to ignore the one night it meant something.
    /// </para>
    /// </remarks>
    public async Task<int> PruneAsync(
        RetentionPolicy policy, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Every value interpolated below is already a string, so nothing here formats a number
        // against a culture. The two that were numbers are converted invariantly first, which is
        // also why they are converted rather than interpolated: string.Create's handler takes a
        // single interpolated literal and refuses a concatenation, and these lines are too long
        // to be one.
        await _output.WriteLineAsync(
            $"Retention: idle after {Hours(policy.IdlePeriod)}, nothing older than " +
            $"{Hours(policy.MaximumAge)}. Set {RetentionPolicyEnvironment.IdleVariable} and " +
            $"{RetentionPolicyEnvironment.MaximumAgeVariable} to change them, in the same " +
            "environment the agents' servers are launched with.").ConfigureAwait(false);

        var result = await _console.PruneAsync(policy, nowUtc, cancellationToken)
            .ConfigureAwait(false);

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
            // holding traffic is worth looking into; another sweep having got there first is the
            // system working — every agent's server prunes on its own schedule, and this command
            // is not the only thing applying the policy.
            var contended = result.Contended.ToString(CultureInfo.InvariantCulture);

            await _output.WriteLineAsync(
                $"{contended} were already gone: an agent's server swept them between this " +
                "command deciding and doing. Nothing is wrong — every server applies the same " +
                "policy on its own schedule.").ConfigureAwait(false);
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

    /// <summary>Renders an enum member the way the registry stores it.</summary>
    /// <param name="value">The member.</param>
    /// <returns>Its lowercase name.</returns>
    private static string Lower(object value) =>
        value.ToString()!.ToLowerInvariant();
}
