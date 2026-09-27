using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Setup;
using Parley.Web.Seating;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// Doing something to a deployment, having first said what it would do.
/// </summary>
/// <remarks>
/// <para>
/// Every action arrives here as a description before it happens. SPEC.md §10.2's purge destroys
/// an agent and every conversation it was party to, including its peers' side of them, and cannot
/// be undone — so an operator who meant retire and clicked purge must have been told, in words,
/// what they were about to lose.
/// </para>
/// <para>
/// A decline is not a failure and is not shown as one. §11.5 says an agent may refuse an
/// instruction, and an interface that rendered refusal as malfunction would teach whoever reads
/// it to work around the agent rather than ask why.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
/// <param name="seats">
/// The seats people hold, because asking an agent what it owns is a message and a message is sent as
/// somebody. Every other action on this page is an operator operation and needs no seat.
/// </param>
public sealed class ActModel(BrokerDirectory directory, SeatBook seats) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>What to do: retire, reinstate, purge, forceconclude, instruct, prune, ownership or askownership.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Action { get; set; }

    /// <summary>Who or what it is about.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Subject { get; set; }

    /// <summary>The operator's words, when instructing.</summary>
    [BindProperty]
    public string? Text { get; set; }

    /// <summary>Why the person is closing a conversation, when they give a reason.</summary>
    /// <remarks>
    /// Told to every participant holding open work in it (§5.3). Optional, because a close with no reason
    /// is still a close each of them is told about, and refusing one would leave the work open instead.
    /// </remarks>
    [BindProperty]
    public string? Reason { get; set; }

    /// <summary>What this action would do, in words, before it is done.</summary>
    public string Warning { get; private set; } = string.Empty;

    /// <summary>Whether it destroys something that cannot be recovered.</summary>
    public bool Destroys { get; private set; }

    /// <summary>What happened, once it has.</summary>
    public string? Outcome { get; private set; }

    /// <summary>Describes what is about to happen. Nothing is done here.</summary>
    /// <returns>The confirmation page.</returns>
    public IActionResult OnGet()
    {
        if (directory.Find(Project) is null || string.IsNullOrWhiteSpace(Subject))
        {
            return RedirectToPage("/Index");
        }

        (Warning, Destroys) = Action?.ToLowerInvariant() switch
        {
            "retire" => ($"'{Subject}' will stop being routed to and will not be listed to its "
                       + "peers. Its history is kept, and reinstating it undoes this.", false),

            "reinstate" => ($"'{Subject}' will be routed to again.", false),

            "purge" => ($"'{Subject}' will be deleted, along with every conversation it was party "
                      + "to — including its peers' side of those conversations. Its name becomes "
                      + "free for another agent. This cannot be undone, and retiring it instead "
                      + "achieves everything except freeing the name.", true),

            "forceconclude" => ($"Conversation '{Subject}' will be closed now, whatever its participants "
                              + "have voted. Every request still open in it is closed with a result saying "
                              + "a person closed it, each participant holding that work is told, and the "
                              + "log records that a person closed it.", false),

            "instruct" => ($"'{Subject}' will be given your words as a turn. It may decline, which "
                         + "is not a failure. The instruction is recorded as yours.", false),

            // Named as a cost rather than as a risk. Nothing is destroyed and nothing is written, so
            // `Destroys` stays false -- but a turn is spent on the agent's side and a person cannot
            // un-spend it, which is the whole of what they are deciding.
            "askownership" => ($"'{Subject}' will be asked what it owns, as one ordinary request from "
                             + "your seat, and answering costs it a turn. Nothing is written by "
                             + "asking: its answer is a message, and you accept it or you do not.", false),

            "prune" => ($"The broker for '{Project}' applies its retention policy now rather than at its next "
                      + "sweep. Message text past the policy is deleted permanently: export a conversation "
                      + "first if it is worth keeping. Nothing on an open exchange, and nothing not yet "
                      + "collected, is removed.", true),

            _ => ("This is not something the console does.", false)
        };

        return Page();
    }

    /// <summary>Does it, having said what it would do.</summary>
    /// <returns>The page, reporting what happened.</returns>
    public async Task<IActionResult> OnPostAsync()
    {
        var project = directory.Find(Project);

        if (project is null || string.IsNullOrWhiteSpace(Subject))
        {
            return RedirectToPage("/Index");
        }

        var broker = new BrokerClient(directory.ClientFor(project));
        var token = HttpContext.RequestAborted;

        // The subject arrives in a URL, so it is whatever was typed there. Every action but
        // forceconclude and prune needs it to be an agent name, and the parse is done once, here,
        // rather than inside the switch: AgentName.Create throws on anything malformed, and an
        // unhandled throw out of a page handler is a stack trace served to whatever asked for it.
        //
        // The count this comment used to carry went stale the first time an action was added, so it
        // names the two exceptions instead -- which is the fact that decides where the guard sits.
        var named = AgentName.TryCreate(Subject, out var agent);

        var read = await BrokerDirectory.ReadAsync(async () =>
            await (Action?.ToLowerInvariant() switch
            {
                // forceconclude takes a conversation identifier, which is opaque and chosen by an
                // agent (§5.0). There is nothing to validate it against, so it is passed through.
                "forceconclude" => Describe(broker.ForceConcludeAsync(
                    Subject, string.IsNullOrWhiteSpace(Reason) ? null : Reason, token)),

                // prune acts on the whole project; its subject is only what made the page's link.
                "prune" => DescribePrune(broker.PruneAsync(token)),

                _ when !named => Task.FromResult(
                    $"'{Subject}' is not a name an agent can have, so there is nothing to act on."),

                "retire" => Describe(broker.RetireAsync(agent, token)),

                "reinstate" => Describe(broker.ReinstateAsync(agent, token)),

                "purge" => DescribePurge(broker.PurgeAsync(agent, token)),

                "instruct" => DescribeInstruction(
                    broker.InstructAsync(agent, Text ?? string.Empty, token)),

                // Composed, because what a person types *here* is their own line and is recorded as
                // theirs. That is still true now the ask exists: a proposal an agent wrote arrives
                // through the conversation it was agreed in and is accepted from that conversation,
                // carrying it -- which is why `source` exists at all rather than every acceptance
                // being one thing.
                "ownership" => AcceptOwnership(broker, agent, Text ?? string.Empty, token),

                // The one action on this page that is a message rather than an operator operation, so
                // the one that needs a seat. It adds nothing to the protocol: an ordinary Request,
                // opening an ordinary exchange, in a conversation named so that a surface can find the
                // discussion again without a marker in anybody's payload (§13).
                "askownership" => AskOwnership(project, agent, token),

                _ => Task.FromResult("This is not something the console does.")
            }).ConfigureAwait(false)).ConfigureAwait(false);

        Outcome = read.Value ?? read.Problem;

        return Page();
    }

    private static async Task<string> Describe(Task<bool> action) =>
        await action.ConfigureAwait(false) ? "Done." : "Nothing changed.";

    /// <summary>Writes the line where it takes effect, then records that a person accepted it.</summary>
    /// <param name="broker">This project's broker.</param>
    /// <param name="agent">Whose line it is.</param>
    /// <param name="statement">What the person typed. Their own words, so recorded as composed.</param>
    /// <param name="token">Cancels the calls.</param>
    /// <returns>What to put in front of the person.</returns>
    /// <remarks>
    /// <para>
    /// <b>The file first and the record second</b>, because a file with no record leaves the line live
    /// and its provenance unrecorded — which the next acceptance repairs — while a record with no file
    /// would claim somebody accepted a line peers will never read, and nothing repairs a wrong record.
    /// </para>
    /// <para>
    /// <b>The workspace comes from the registry, which is what the agent said about itself</b> and may
    /// be long out of date. That is why the write reads the name back out of the file before touching
    /// it: 005's review gate found a withdrawal deleting a <em>different</em> agent's launch
    /// configuration for exactly this reason.
    /// </para>
    /// </remarks>
    private static async Task<string> AcceptOwnership(
        BrokerClient broker, AgentName agent, string statement, CancellationToken token)
    {
        if (OwnershipLine.NotHere(agent) is { } elsewhere)
        {
            return $"Not recorded: {elsewhere}";
        }

        var registry = await broker.RegistryAsync(token).ConfigureAwait(false);
        var known = registry.FirstOrDefault(r => r.Name == agent);

        if (known is null)
        {
            return $"Not recorded: nothing has registered under '{agent.Value}', so there is no " +
                   "repository recorded for it and no line to write.";
        }

        var wrong = await OwnershipLine
            .WriteAsync(known.Workspace, null, agent, statement, token)
            .ConfigureAwait(false);

        if (wrong is not null)
        {
            return $"Not recorded: {wrong}";
        }

        return await DescribeOwnership(
            broker.RecordOwnershipAsync(
                agent,
                new OwnershipAcceptance(statement, OwnershipSource.Composed, null, null),
                token)).ConfigureAwait(false);
    }

    /// <summary>Asks an agent what it owns, from the seat the person holds.</summary>
    /// <param name="project">Which project's broker to ask through.</param>
    /// <param name="agent">Who to ask.</param>
    /// <param name="token">Cancels the send.</param>
    /// <returns>What to put in front of the person.</returns>
    /// <remarks>
    /// <para>
    /// <b>A seat is required and its absence is said plainly</b>, because §13 rests on a receiver
    /// being able to tell who spoke: an operator instruction in place of a message would put the
    /// question in front of the agent with nothing in the transcript and nothing owed back.
    /// </para>
    /// <para>
    /// <b>The machine rule deliberately does not apply here.</b> <c>OwnershipLine.NotHere</c> guards a
    /// write to a file on the agent's machine, and asking is not that — a broker reaches agents on
    /// machines this console will never touch, and refusing to ask them would withhold the operation
    /// from exactly the deployment it is worth most in.
    /// </para>
    /// </remarks>
    private async Task<string> AskOwnership(
        ProjectSettings project, AgentName agent, CancellationToken token)
    {
        var here = SeatCookie.Read(Request);
        var seated = here is null ? null : seats.Held(project.Name.Value, here);

        if (seated is null)
        {
            return "Nothing was asked. An ask is a message, so it is sent as somebody, and no seat "
                 + "is held here: take one on the inbox page and ask again. The agent's answer then "
                 + "comes back to that name.";
        }

        var conversation = OwnershipAsk.Conversation(agent, DateTimeOffset.UtcNow);

        var said = await new SeatVoice(directory.AgentsFor(project), seated.Seat)
            .RequestAsync(agent, OwnershipAsk.Text, conversation, token)
            .ConfigureAwait(false);

        return said + " Its answer arrives in conversation " + conversation
             + ", which is where it is read and accepted.";
    }

    /// <summary>Says what became of an accepted ownership line.</summary>
    /// <param name="action">The call.</param>
    /// <returns>What to put in front of the person.</returns>
    /// <remarks>
    /// <b>It has to say that nothing an asker reads has changed yet</b>, which is the one thing a
    /// person would otherwise assume. The registry is filled from the launch environment when the
    /// agent attaches (§12.3), so an accepted line is a decision until the agent next starts —
    /// reporting "Done." alone would be true about the record and false about the deployment.
    /// </remarks>
    private static async Task<string> DescribeOwnership(Task<OwnershipRecord> action)
    {
        var record = await action.ConfigureAwait(false);

        if (!record.Recorded)
        {
            return $"Not recorded: {record.Problem}";
        }

        return record.Pending
            ? "Recorded. Peers still read " +
              (record.LiveStatement is null ? "no line for this agent" : $"'{record.LiveStatement}'") +
              " until it next starts, because the line comes from its launch configuration."
            : "Recorded, and it is what peers already read.";
    }

    private static async Task<string> DescribePrune(Task<PruneResult> action)
    {
        var pruned = await action.ConfigureAwait(false);

        var said = $"Removed {pruned.Removed} message(s)"
                   + (pruned.InstructionsRemoved > 0 ? $" and {pruned.InstructionsRemoved} instruction(s)." : ".");

        if (pruned.HeldByFloor > 0)
        {
            said += $" {pruned.HeldByFloor} more were old enough and were kept, because they are on open work, not yet collected, or the request a result not yet collected carries back.";
        }

        return pruned.Capped ? said + " It stopped at its own limit with more still to remove; run it again." : said;
    }

    private static async Task<string> DescribePurge(Task<PurgeOutcome> action)
    {
        var outcome = await action.ConfigureAwait(false);

        return outcome.Verdict == PurgeVerdict.NotRegistered
            ? "There is no such agent."
            : "Purged: " + string.Join(", ",
                outcome.Destroyed.Select(d => $"{d.Rows} from {d.Table}"));
    }

    /// <summary>Puts an instruction's outcome into a sentence.</summary>
    /// <param name="action">The call in flight.</param>
    /// <returns>What to tell the operator.</returns>
    /// <remarks>
    /// <para>
    /// <b>Five outcomes, five sentences, and the fifth used to be unreachable.</b> This method
    /// ended with <c>_ =&gt; "Still pending."</c> — a branch nothing could produce, because the
    /// broker turned its own impatience into <c>failed</c> and settled the record with it. So the
    /// operator was told the turn had not completed while the agent was still working, and the real
    /// answer was refused when it arrived.
    /// </para>
    /// <para>
    /// <b>The page no longer waits for the turn.</b> It waits for the broker's own short patience
    /// and then says where the answer will appear. A turn is a model working on a repository and
    /// can run for as long as the machine hosting it allows; a browser request cannot, and tying
    /// the two together is how a lost connection came to mean a lost answer.
    /// </para>
    /// </remarks>
    private static async Task<string> DescribeInstruction(Task<InstructionResult> action)
    {
        var result = await action.ConfigureAwait(false);

        // Declined is deliberately not phrased as a failure. §10.2 requires the two told apart, and
        // an interface that renders refusal as malfunction teaches whoever reads it to work around
        // the agent instead of asking why.
        return result.Outcome switch
        {
            InstructionOutcome.Taken => $"Taken. {result.Reply}",
            InstructionOutcome.Declined => $"The agent declined, which is its right. {result.Reply}",
            InstructionOutcome.Undeliverable =>
                InstructionWording.Undeliverable,
            InstructionOutcome.Failed => $"The turn did not complete. {result.Reply}",

            // The ordinary answer for any real piece of work, and it must not read as a fault.
            _ => "Given to the agent, and the turn is still running. Its answer will appear under "
                 + "What you have told it on the agent's page — you can close this."
        };
    }
}
