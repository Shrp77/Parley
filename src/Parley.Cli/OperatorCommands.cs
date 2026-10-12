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
        // Read through `BrokerClient` rather than off the wire directly, which is what puts the pending
        // mark here: the rule for "accepted but not yet live" is a comparison of two observations
        // (`AgentRecord.OwnsPending`), and spelling that comparison a second time here is how the three
        // surfaces come to disagree about the state of one agent. 020's quickstart walk found this
        // listing showing the live line with nothing said about a line already accepted, which FR-020
        // forbids of any surface showing the line — and this is the listing somebody runs to check that
        // an acceptance worked.
        // Parley is left out: it is the project's own participant rather than an agent, owns nothing and is started by
        // nothing, so a row for it here would read as an agent nobody can reach (SPEC.md §14). Every store holds it, so
        // counting it would also make "no agents registered" impossible to say.
        var agents = (await new Parley.Client.BrokerClient(_console)
            .RegistryAsync(cancellationToken)
            .ConfigureAwait(false))
            .Where(a => !a.Facilitator)
            .ToList();

        // The person seat is listed (FR-006: every listing shows the person) but is not an agent, so it does not count
        // towards "no agents registered" — every store holds one from the moment it is served, and counting it
        // would make that sentence impossible to say, exactly as counting Parley would.
        if (!agents.Any(a => !PersonSeat.Is(a.Name)))
        {
            await _output.WriteLineAsync(
                "No agents registered. One appears the first time its server starts.")
                .ConfigureAwait(false);
        }

        foreach (var agent in agents.OrderBy(a => a.Name.Value, StringComparer.Ordinal))
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
            //
            // The mark is `Roster.PendingMark`, which is the one sentence the other two surfaces
            // append, rather than a wording of its own: three spellings of one state is three
            // chances for a reader to think they mean different things.
            //
            // Not for the person, who owns no repository and whom storage refuses a line: telling somebody
            // to set PARLEY_OWNS for themselves would send them to do something that cannot be done.
            if (PersonSeat.Is(agent.Name))
            {
                continue;
            }

            await _output.WriteLineAsync(
                $"  owns: {(agent.Owns is { Length: > 0 } owns ? owns : $"(unset — set {DeploymentEnvironment.OwnsVariable} where that agent's server is launched)")}"
                + Parley.Client.Rendering.Roster.PendingMark(agent))
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
    /// Reports what Results said, per SPEC.md §10.2.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Zero. The report is the output.</returns>
    /// <remarks>
    /// <para>
    /// Four tables, because they answer four questions somebody running a deployment asks: what came of the
    /// work, what stopped it, whose word it rested on, and how conversations ended.
    /// </para>
    /// <para>
    /// Blockers are written so a high number reads as a question rather than a verdict. An agent blocked on
    /// permission again and again is a boundary people should redraw, not an agent behaving badly.
    /// </para>
    /// </remarks>
    public async Task<int> ResultsAsync(CancellationToken cancellationToken)
    {
        var report = await _console.DescribeResultsAsync(new Wire.Empty(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (report.Outcomes.Count == 0 && report.Closures.Count == 0)
        {
            await _output.WriteLineAsync(
                "No results have been sent and no conversation has closed yet.").ConfigureAwait(false);

            return 0;
        }

        await _output.WriteLineAsync("agent                responder              outcome        items")
            .ConfigureAwait(false);

        foreach (var row in report.Outcomes)
        {
            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.Agent,-20} {row.ResponderModel,-22} {Snake(row.Outcome),-14} {row.Items,5}"))
                .ConfigureAwait(false);
        }

        if (report.Blockers.Count > 0)
        {
            await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
            await _output.WriteLineAsync("blocked                by                     items").ConfigureAwait(false);

            foreach (var row in report.Blockers)
            {
                await _output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{row.Agent,-22} {Snake(row.Kind),-22} {row.Items,5}")).ConfigureAwait(false);
            }
        }

        if (report.Sources.Count > 0)
        {
            await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
            await _output.WriteLineAsync("agent                relied on            items").ConfigureAwait(false);

            foreach (var row in report.Sources)
            {
                await _output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{row.Agent,-20} {row.Source,-20} {row.Items,5}")).ConfigureAwait(false);
            }
        }

        if (report.Closures.Count > 0)
        {
            await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
            await _output.WriteLineAsync("agent                by agreement  by a person").ConfigureAwait(false);

            foreach (var row in report.Closures)
            {
                await _output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{row.Agent,-20} {row.ByConsensus,12}  {row.ByPerson,11}")).ConfigureAwait(false);
            }
        }

        return 0;
    }

    /// <summary>
    /// Reports open work that needs a person, per SPEC.md §10.2.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Zero. The report is the output.</returns>
    /// <remarks>
    /// Nothing in the protocol times out, so this is how work nobody collected, or collected and left, comes
    /// to anybody's attention. Each line names the conversation, because closing it is the remedy.
    /// </remarks>
    public async Task<int> FlagsAsync(CancellationToken cancellationToken)
    {
        var report = await _console.DescribeFlagsAsync(new Wire.Empty(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (report.Flags.Count == 0 && report.Cycles.Count == 0)
        {
            await _output.WriteLineAsync(
                "Nothing is flagged. All open work has been collected and is moving.").ConfigureAwait(false);

            return 0;
        }

        foreach (var flag in report.Flags)
        {
            if (flag.Kind == Wire.FlagKind.NotVoted)
            {
                // No exchange to name: the work is all closed, and the conversation waits on a vote.
                await _output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Readable(flag.SinceUtc)}  {flag.ConversationId}: its work is all closed and {flag.Waiting} " +
                    $"is finished; closing it is waiting on {flag.WaitingOn} to say whether it is too")).ConfigureAwait(false);

                continue;
            }

            var what = flag.Kind switch
            {
                Wire.FlagKind.NotCollected => "has not collected it",
                Wire.FlagKind.NoProgress => "collected it and has done nothing since",

                // SPEC.md §6: raised at once, because only a person can get it past this.
                Wire.FlagKind.PermissionRefused =>
                    "stopped after a tool call it needed was refused for want of permission; allow it in that " +
                    "repository's settings, then start the agent again",
                _ => "is retired and still holds it"
            };

            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{Readable(flag.SinceUtc)}  {flag.ConversationId}  exchange {flag.ExchangeId}: " +
                $"{flag.Waiting} is waiting on {flag.WaitingOn}, which {what}")).ConfigureAwait(false);
        }

        if (report.Cycles.Count > 0)
        {
            await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
            await _output.WriteLineAsync("requests that went round in a circle").ConfigureAwait(false);

            foreach (var cycle in report.Cycles)
            {
                await _output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Readable(cycle.RecordedUtc)}  {cycle.ConversationId}  {string.Join(" -> ", cycle.Path)}"))
                    .ConfigureAwait(false);
            }
        }

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await _output.WriteLineAsync(
            "Ask the agent why, or close the conversation with `parley force-conclude <conversation> <reason>`.")
            .ConfigureAwait(false);

        return 0;
    }

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

        // "none" rather than the number that spells it. The default is no cap, stored as the largest
        // integer (ExchangePolicy.UnboundedClarifications), and a report reading "2147483647" would
        // tell somebody deciding about a cap that one was set, at a value nobody chose.
        var lowest = CapInForce(report.LowestCapInForce);
        var highest = CapInForce(report.HighestCapInForce);

        await _output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{Exchanges(report)} exchanges. Caps in force: " +
            $"{(lowest == highest ? lowest : $"{lowest} to {highest}")}.")).ConfigureAwait(false);

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
            $"Results mixing outcomes across items:        " +
            $"{report.ResultsWithMixedOutcomes} of {report.Results}"))
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

    /// <summary>A cap as a person reads it: its number, or "none" when the exchange is uncapped.</summary>
    /// <param name="cap">The cap stamped on an exchange.</param>
    /// <returns>The words.</returns>
    private static string CapInForce(long cap) =>
        cap == Parley.Core.ExchangePolicy.UnboundedClarifications
            ? "none"
            : cap.ToString(CultureInfo.InvariantCulture);

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
            "server will be refused. Work it still holds stays open and is flagged as held by a " +
            "retired agent (`parley flags`): close those conversations, or reinstate it.")
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
                $"No messages in '{conversationId}'. A conversation exists only once a request " +
                "has been sent in it.").ConfigureAwait(false);

            return 1;
        }

        foreach (var message in messages)
        {
            var text = new StringBuilder()
                .Append(message.CreatedUtc)
                .Append("  ")
                // Wider than the longest type, "request" and "clarify" at seven, so the type never
                // runs into the sender. It once did, padded to its own length; found by reading the output.
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

    /// <summary>Closes a conversation now, whatever its participants have voted.</summary>
    /// <param name="conversationId">The conversation to close.</param>
    /// <param name="reason">Why, told to every participant holding open work; null for none.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Zero if it closed, one if it did not.</returns>
    /// <remarks>
    /// Named <c>force-conclude</c> rather than <c>conclude</c>, following §10.2. Nothing in the protocol
    /// times out, so this is how a person ends work that has stalled — a deliberate asymmetry rather than a
    /// hole in principle 2.6: no agent may conclude over another's objection, and the person running the
    /// broker is not an agent.
    /// </remarks>
    public async Task<int> ForceConcludeAsync(
        string conversationId, string? reason, CancellationToken cancellationToken)
    {
        var concluded = await _console.ForceConcludeAsync(
            new Wire.ForceConcludeRequest { ConversationId = conversationId, Reason = reason ?? string.Empty },
            cancellationToken: cancellationToken);

        if (!concluded.Changed)
        {
            await _output.WriteLineAsync(
                $"'{conversationId}' is not open, or does not exist.").ConfigureAwait(false);

            return 1;
        }

        await _output.WriteLineAsync(
            $"'{conversationId}' is closed. Every request still open in it was closed with a result " +
            "saying a person closed it, and each participant holding that work was told. A new " +
            "request naming it will be refused.").ConfigureAwait(false);

        return 0;
    }

    /// <summary>Asks an agent's machine to start it.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Zero if a start was asked for, one if nothing could start it.</returns>
    /// <remarks>
    /// Named apart from the broker's <c>StartAsync</c>, which is an agent attaching (§10.1), for the reason §10.2
    /// gives. It says what it asked for rather than that the agent is running: registration is the agent's own act
    /// on attach, and <c>parley registry</c> is where that shows.
    /// </remarks>
    public async Task<int> StartAgentAsync(AgentName agent, CancellationToken cancellationToken)
    {
        var reply = await _console.StartAsync(
            new Wire.StartRequest { Agent = agent.Value, Because = "an operator started it from the command line" },
            cancellationToken: cancellationToken);

        if (!reply.Changed)
        {
            await _output.WriteLineAsync(
                $"Nothing could start '{agent.Value}': its machine's node is not connected, or its catalogue does not name it.")
                .ConfigureAwait(false);

            return 1;
        }

        await _output.WriteLineAsync(
            $"Asked the machine that holds '{agent.Value}' to start it. It registers itself as it attaches; " +
            "`parley registry` shows when it has.").ConfigureAwait(false);

        return 0;
    }

    /// <summary>Takes a conversation off the list.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Zero if it changed, one if it did not.</returns>
    public async Task<int> ArchiveAsync(string conversationId, CancellationToken cancellationToken)
    {
        var reply = await _console.ArchiveAsync(
            new Wire.ConversationRequest { ConversationId = conversationId }, cancellationToken: cancellationToken);

        await _output.WriteLineAsync(reply.Changed
            ? $"'{conversationId}' is archived. Nothing was deleted, and it stays archived if more is said in it."
            : $"'{conversationId}' does not exist, or was already archived.").ConfigureAwait(false);

        return reply.Changed ? 0 : 1;
    }

    /// <summary>Puts an archived conversation back on the list.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Zero if it changed, one if it did not.</returns>
    public async Task<int> UnarchiveAsync(string conversationId, CancellationToken cancellationToken)
    {
        var reply = await _console.UnarchiveAsync(
            new Wire.ConversationRequest { ConversationId = conversationId }, cancellationToken: cancellationToken);

        await _output.WriteLineAsync(reply.Changed
            ? $"'{conversationId}' is back in the list."
            : $"'{conversationId}' does not exist, or was not archived.").ConfigureAwait(false);

        return reply.Changed ? 0 : 1;
    }

    /// <summary>Deletes a conversation and every message in it, for every participant.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Zero if it was deleted, one if there was no such conversation.</returns>
    /// <remarks>
    /// Per table afterwards, like <see cref="PurgeAsync"/>, and for its reason: nothing here is a confirmation
    /// prompt, so what an operator needs is an account of what is now missing.
    /// </remarks>
    public async Task<int> DestroyAsync(string conversationId, CancellationToken cancellationToken)
    {
        var reply = await _console.DestroyAsync(
            new Wire.ConversationRequest { ConversationId = conversationId }, cancellationToken: cancellationToken);

        if (reply.Verdict != Wire.DestroyVerdict.Gone)
        {
            await _output.WriteLineAsync($"There is no conversation called '{conversationId}'.").ConfigureAwait(false);

            return 1;
        }

        await _output.WriteLineAsync($"'{conversationId}' is destroyed, and with it:").ConfigureAwait(false);

        foreach (var toll in reply.Destroyed)
        {
            await _output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  {toll.Table}  {toll.Rows}"))
                .ConfigureAwait(false);
        }

        await _output.WriteLineAsync(
            "This cannot be undone. Anybody waiting on an open request in it is never told anything.").ConfigureAwait(false);

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
                "that has not closed, the request a result not yet collected carries back, or " +
                "inside the deduplication window. That floor is not " +
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

    /// <summary>Renders a wire enum member the way the protocol spells it: <c>missing_information</c>.</summary>
    /// <param name="value">The member.</param>
    /// <returns>Its snake-case name.</returns>
    private static string Snake(Enum value)
    {
        var name = value.ToString();
        var written = new StringBuilder(name.Length + 4);

        foreach (var character in name)
        {
            if (char.IsUpper(character) && written.Length > 0)
            {
                written.Append('_');
            }

            written.Append(char.ToLowerInvariant(character));
        }

        return written.ToString();
    }

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

    /// <summary>Renders an enum member the way the registry stores it.</summary>
    /// <param name="value">The member.</param>
    /// <returns>Its lowercase name.</returns>
    private static string Lower(object value) =>
        value.ToString()!.ToLowerInvariant();
}
