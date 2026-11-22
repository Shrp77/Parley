using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Setup;

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
/// </remarks>
/// <param name="directory">
/// The projects and their brokers. No seat is needed: Parley asks an agent what it owns, not the person.
/// </param>
public sealed class ActModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>What retiring and purging reach: one project's store, never the others.</summary>
    private string ThisProjectOnly => AgentWithdrawal.ThisProjectOnly(Project);

    /// <summary>The agent's name, typed back, for retiring, reinstating and purging.</summary>
    /// <remarks>
    /// The terminal's door, here too: a description and then the thing's own name, checked by
    /// <see cref="AgentWithdrawal.TypedNameProblem"/> on both surfaces.
    /// </remarks>
    [BindProperty, FromForm]
    public string? Typed { get; set; }

    /// <summary>Whether the person accepted purging an agent that still owes work.</summary>
    [BindProperty, FromForm]
    public bool AcceptOwed { get; set; }

    /// <summary>Whether this action is one of the three that withdraw an agent and take its name typed back.</summary>
    public bool Withdraws => Action?.ToLowerInvariant() is "retire" or "reinstate" or "purge";

    /// <summary>What the agent owes right now, read for retiring and purging, or null when not read.</summary>
    public string? Owes { get; private set; }

    /// <summary>What purging it would do to whoever waits on it, or null when it owes nothing.</summary>
    public string? OwedConsequence { get; private set; }

    /// <summary>
    /// What to do: one of the arms of the two switches below.
    /// </summary>
    /// <remarks>
    /// <c>EveryActionHasADoorTests</c> reads the arms out of both switches and out of every page,
    /// so the set is held there and not in any list.
    /// </remarks>
    [BindProperty(SupportsGet = true)]
    public string? Action { get; set; }

    /// <summary>Who or what it is about.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Subject { get; set; }

    /// <summary>The person's own words: the ownership line they are recording.</summary>
    /// <remarks>
    /// What makes the ownership line theirs is that nothing pre-fills this box — see <c>Act.cshtml</c>.
    /// </remarks>
    [BindProperty, FromForm]
    public string? Text { get; set; }

    /// <summary>The agents typing <c>@</c> offers in a reply to a question asked back, read only for that action.</summary>
    public IReadOnlyList<string> Mentionable { get; private set; } = [];

    /// <summary>Whether a freeze outlasts a logon: the box that says not to unfreeze on logon.</summary>
    /// <remarks>
    /// Without it, a start of the broker, which a logon or a reboot brings, lets the freeze lapse and the agent is started if it owes
    /// work (SPEC.md §14). Posted with the confirmation, never read from the link, so a followed link cannot choose it.
    /// </remarks>
    [BindProperty, FromForm]
    public bool KeepFrozen { get; set; }

    /// <summary>Why the person is closing a conversation, when they give a reason.</summary>
    /// <remarks>
    /// Told to every participant holding open work in it (§5.3). Optional, because a close with no reason
    /// is still a close each of them is told about, and refusing one would leave the work open instead.
    /// </remarks>
    [BindProperty, FromForm]
    public string? Reason { get; set; }

    /// <summary>What this action would do, in words, before it is done.</summary>
    public string Warning { get; private set; } = string.Empty;

    /// <summary>Whether it destroys something that cannot be recovered.</summary>
    public bool Destroys { get; private set; }

    /// <summary>What happened, once it has.</summary>
    public string? Outcome { get; private set; }

    /// <summary>The projects a freeze or an unfreeze of the agent reaches, said before the person agrees.</summary>
    public string? Reaches { get; private set; }

    /// <summary>The agent's answer to Parley, on one line, when the action is accepting it.</summary>
    /// <remarks>
    /// The one box on this page that opens holding somebody else's words, and it says whose. Leaving it unchanged records
    /// the agent's words as the agent's; changing a letter records a person's edit. That is a comparison made at the
    /// POST, never a flag, and it is the distinction the <c>ownership</c> action's empty box exists to keep.
    /// </remarks>
    public string? Proposed { get; private set; }

    /// <summary>Whether the act is offered at all, or only explained: false for a kill of a process no node that is gone left.</summary>
    public bool Offered { get; private set; } = true;

    /// <summary>Whether the agent a freeze names runs as a process a node that is gone left, which the freeze does not end.</summary>
    public bool FreezingLeft { get; private set; }

    /// <summary>Whether the agent a freeze names is open in an editor Parley did not start, which the freeze does not end.</summary>
    public bool FreezingOutside { get; private set; }

    /// <summary>Whether the agent a kill names is frozen, so it is not told new work starts it again.</summary>
    public bool KillingFrozen { get; private set; }

    /// <summary>Describes what is about to happen. Nothing is done here.</summary>
    /// <returns>The confirmation page.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        var project = directory.Find(Project);

        if (project is null || string.IsNullOrWhiteSpace(Subject))
        {
            return RedirectToPage("/Index");
        }

        // The one description that needs the broker: accepting an answer shows the answer. Read here so the box can hold
        // the agent's words, and so a person is told plainly when there is nothing waiting rather than shown an empty box.
        if (string.Equals(Action, "acceptanswer", StringComparison.OrdinalIgnoreCase)
            && AgentName.TryCreate(Subject, out var answering))
        {
            var read = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project)).FacilitationAsync(HttpContext.RequestAborted)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            Proposed = read.Value?.Questions
                .FirstOrDefault(q => q.Agent == answering && q.State == QuestionState.Waiting)?.Flattened;
        }

        if (string.Equals(Action, "replyaskedback", StringComparison.OrdinalIgnoreCase))
        {
            var roll = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project)).RegistryAsync(HttpContext.RequestAborted)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            Mentionable = [.. Parley.Client.Rendering.RoomRoster.NotInIt(roll.Value, [], PersonSeat.On(project.Name)).Select(agent => agent.Value)];
        }

        // Ending a process is described only for one a node that is gone left running (SPEC.md §11.1): for any other the
        // description would be untrue and the broker refuses the act, so neither is offered. A registry that cannot be read
        // leaves it offered, and the broker's own check decides.
        // Lower-cased as the description and the act read it, never compared ignoring case: those differ for a Kelvin sign.
        if (Action?.ToLowerInvariant() == "kill" && AgentName.TryCreate(Subject, out var killing))
        {
            var roll = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project)).RegistryAsync(HttpContext.RequestAborted)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            Offered = roll.Value is not { } rows
                || rows.Any(row => row.Name == killing && row.Presence == AgentPresence.Running && row.Unsupervised);
            KillingFrozen = roll.Value?.Any(row => row.Name == killing && row.Frozen) ?? false;
        }

        // Freezing ends the one process that serves every project the agent is in, so the person is told which they are before
        // they agree (SPEC.md §14): freezing it for one project's sake stops its work for the rest.
        if (Action?.ToLowerInvariant() is "freeze" or "unfreeze" && AgentName.TryCreate(Subject, out var reached))
        {
            var (serving, unreachable) = await AgentFreezing.ServedByAsync(directory, reached, HttpContext.RequestAborted)
                .ConfigureAwait(false);

            Reaches = AgentFreezing.Reach(reached, serving, unreachable);

            // What holds its process, so the words before a freeze promise only the stop that can happen.
            var held = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project)).RegistryAsync(HttpContext.RequestAborted)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            if (held.Value?.FirstOrDefault(row => row.Name == reached && row.Presence == AgentPresence.Running) is { } running)
            {
                FreezingLeft = running.Unsupervised;
                FreezingOutside = !running.Unsupervised && running.OpenOutside;
            }
        }

        // What it owes, as the terminal's dialog says it: the debt is what makes the difference between retiring and
        // purging concrete, and a purge of an agent that still owes work asks for an acceptance first.
        if (Action?.ToLowerInvariant() is "retire" or "purge" && AgentName.TryCreate(Subject, out var leaving))
        {
            var owed = await OwedAsync(new BrokerClient(directory.ClientFor(project)), leaving, HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (owed is not null)
            {
                Owes = AgentWithdrawal.Owing(owed);
                OwedConsequence = owed.Count > 0 ? AgentWithdrawal.OwedConsequence(leaving.Value, owed) : null;
            }
        }

        (Warning, Destroys) = Action?.ToLowerInvariant() switch
        {
            // The terminal's words, from the one place both surfaces read them.
            "retire" => (AgentWithdrawal.Retiring + " Reinstating it undoes this. " + AgentWithdrawal.TheFiles + " "
                       + ThisProjectOnly, false),

            "reinstate" => ($"'{Subject}' will be routed to again. " + AgentWithdrawal.Reinstated, false),

            "start" => ($"The node on '{Subject}'s machine is asked to start it. It costs no turn. A retired agent is "
                      + "not started, and nothing can be asked of a machine whose node is not running.", false),

            // Not destructive: what it owes stays owed. What is lost is what the session had learned, which is said.
            "stop" => (Parley.Client.Rendering.StoppingWords.Stopping(Subject), false),

            "restart" => (Parley.Client.Rendering.StoppingWords.Restarting(Subject), false),

            // Not destructive: nothing it owes is closed, moved or answered, and Unfreeze undoes it. What is lost is only what its
            // process was doing once its turn has ended, which is said.
            "freeze" => (Parley.Client.Rendering.FreezingWords.Freezing(Subject, leftRunning: FreezingLeft, openOutside: FreezingOutside) + " " + FreezeReach(), false),

            "unfreeze" => (Parley.Client.Rendering.FreezingWords.Unfreezing(Subject) + " " + FreezeReach(), false),

            "freezeall" => (Parley.Client.Rendering.FreezingWords.FreezingAll(Project ?? string.Empty), false),

            "unfreezeall" => (Parley.Client.Rendering.FreezingWords.UnfreezingAll(Project ?? string.Empty), false),

            // Ends a process now: not destructive of anything the agent owes, but what it is part-way through is lost, said.
            "kill" => (Offered
                ? Parley.Client.Rendering.StoppingWords.Killing(Subject, KillingFrozen)
                : Parley.Client.Rendering.StoppingWords.NothingToKill(Subject), false),

            "purge" => (AgentWithdrawal.Purging + " Its name becomes free for another agent; retiring it instead "
                      + "achieves everything except freeing the name, and keeps its peers' side of those conversations. "
                      + AgentWithdrawal.TheFiles + " " + ThisProjectOnly, true),

            "forceconclude" => ($"Conversation '{Subject}' will be closed now, whatever its participants "
                              + "have voted. Every request still open in it is closed with a result saying "
                              + "a person closed it, and each participant holding that work is told. "
                              + Parley.Client.Rendering.RetentionWords.WhatClosingKeeps, false),

            // The project-level form, which follows `prune` in taking its subject from whatever made the
            // link. Named as a cost rather than as a risk: nothing is destroyed and nothing is written,
            // and a person cannot un-spend a turn per agent.
            "askeveryownership" => ("Parley will ask every agent in this project again what it owns, each as "
                                  + "its own question, and answering costs each of them a turn. An agent whose "
                                  + "answer is still being decided is left alone. No seat is needed: Parley "
                                  + "asks, not you.", false),

            // Named as a cost rather than as a risk. Nothing is destroyed, so `Destroys` stays false -- but a turn is
            // spent on the agent's side and a person cannot un-spend it, which is the whole of what they are deciding.
            "askownership" => ($"Parley will ask '{Subject}' again what it owns, and answering costs it a turn. "
                             + "What becomes of the answer depends on this project's Auto Accept switch, and it "
                             + "shows in the ownership review either way. No seat is needed: Parley asks, "
                             + "not you.", false),

            // The agent's own words, which a person may leave as they are or edit. Not destructive: it replaces one line
            // in one launch entry, and peers go on reading the old one until the agent next starts.
            "acceptanswer" => Proposed is null
                ? ($"Nothing from '{Subject}' is waiting to be accepted.", false)
                : ($"'{Subject}'s answer to Parley, below in its own words, becomes its line and is written into its "
                   + "launch configuration on this machine. Leave it as it is and it is recorded as the agent's "
                   + "words; change anything and it is recorded as your edit. Peers read the new line when the "
                   + "agent next starts.", false),

            "discardanswer" => ($"'{Subject}'s answer to Parley is discarded. Nothing is written, it keeps whatever "
                              + "line it had, and it is not asked again unless a person asks.", false),

            // A person's words, sent through Parley because only the question's requester may reply on its exchange
            // (§5.2), and recorded as the person's -- never as Parley's.
            "replyaskedback" => ($"'{Subject}' asked Parley something back instead of answering. What you write is "
                               + "sent through Parley and recorded as your words, never as Parley's, and it "
                               + "costs the agent a turn to read.", false),

            // Acts on the project, so its subject is the switch's new position rather than an agent.
            "autoaccept" => (string.Equals(Subject, "on", StringComparison.OrdinalIgnoreCase)
                ? $"Auto Accept will be ON for '{Project}': an agent's answer to Parley becomes its line with no "
                  + "person reading it, unless it cannot be one. Each such line is marked as accepted "
                  + "automatically and can be corrected in the ownership review. Answers already waiting stay "
                  + "waiting."
                : $"Auto Accept will be OFF for '{Project}': every answer to Parley waits in the ownership review "
                  + "for a person. Answers already waiting stay waiting.", false),

            // Described here as well as done below, because an action with an arm in the POST and none
            // here would reach a person as "this is not something the console does" above a button that
            // does it anyway. Not destructive: it replaces one line in
            // one launch entry, and the agent's peers go on reading the old one until it restarts.
            "ownership" => ($"The line you write is recorded as yours and written into '{Subject}'s "
                          + "launch configuration on this machine. Its peers go on reading whatever "
                          + "line it started with until it next starts.", false),

            "prune" => ($"The broker for '{Project}' applies its retention policy now rather than at its next "
                      + "retention pass. Message text past the policy is deleted permanently: export a conversation "
                      + "first if it is worth keeping. " + Parley.Client.Rendering.RetentionWords.WhatItKeeps, true),

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
        // The two exceptions decide where the guard sits.
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

                // **Above the name guard deliberately**, for the reason prune is: this acts on every
                // agent the registry holds, so the subject in the URL is what made the link and not
                // something to validate. Putting it below would refuse the whole project because one
                // incidental subject was not a name.
                "askeveryownership" => AskEveryOwnership(broker, token),

                // Above the name guard, as prune is: acts on the project's agents, and its subject is only what made the link.
                "freezeall" => FreezeEveryone(freeze: true, token),
                "unfreezeall" => FreezeEveryone(freeze: false, token),

                // Above the name guard for the same reason: its subject is the switch's new position, not an agent.
                "autoaccept" => DescribeAutoAccept(
                    broker.SetAutoAcceptAsync(string.Equals(Subject, "on", StringComparison.OrdinalIgnoreCase), token),
                    project),

                _ when !named => Task.FromResult(
                    $"'{Subject}' is not a name an agent can have, so there is nothing to act on."),

                "retire" or "reinstate" or "purge" => Withdraw(project, broker, agent, token),

                "start" => DescribeStart(broker.StartAsync(agent, "a person asked from the web UI", token)),

                "freeze" => FreezeOne(agent, freeze: true, token),

                "unfreeze" => FreezeOne(agent, freeze: false, token),

                "stop" => DescribeStop(agent, restart: false, broker.StopAsync(agent, "a person stopped it from the web UI", token)),

                "restart" => DescribeStop(
                    agent, restart: true, broker.RestartAsync(agent, "a person restarted it from the web UI", token)),

                "kill" => DescribeKill(agent, broker.KillAsync(agent, "a person ended it from the web UI", token)),

                // Composed, because what a person types *here* is their own line and is recorded as
                // theirs. A proposal an agent wrote arrives through the conversation it was agreed
                // in and is accepted from that conversation, carrying it -- which is why `source`
                // exists rather than every acceptance being one thing.
                "ownership" => AcceptOwnership(
                    project, broker, agent,
                    new OwnershipAcceptance((Text ?? string.Empty).Trim(), OwnershipSource.Composed, null, null),
                    token),

                // Parley asks, so no seat.
                "askownership" => DescribeAction(broker.AskAgainAsync(agent, token),
                    $"Parley asked '{agent.Value}' what it owns. Its answer shows in the ownership review."),

                "acceptanswer" => AcceptAnswer(project, broker, agent, Text ?? string.Empty, token),

                "discardanswer" => DescribeAction(broker.DiscardAnswerAsync(agent, token),
                    "Discarded. Nothing was written, and it keeps whatever line it had."),

                "replyaskedback" => DescribeAction(broker.ReplyToAskedBackAsync(agent, Text ?? string.Empty, token),
                    "Sent through Parley as your words. Its answer shows in the ownership review when it comes."),

                _ => Task.FromResult("This is not something the console does.")
            }).ConfigureAwait(false)).ConfigureAwait(false);

        Outcome = read.Value ?? read.Problem;

        return Page();
    }

    private static async Task<string> Describe(Task<bool> action) =>
        await action.ConfigureAwait(false) ? "Done." : "Nothing changed.";

    // What a start reports is that a node was asked, never that the agent is running: whether it attaches and registers is
    // a different question, so a screen must not predict a registration.
    private static async Task<string> DescribeStart(Task<bool> start) =>
        await start.ConfigureAwait(false)
            ? "Its node was asked to start it."
            : "Nothing was asked: it is retired, so reinstate it first, or no node on its machine is running.";

    private string FreezeReach() => Reaches ?? string.Empty;

    private async Task<string> FreezeOne(AgentName agent, bool freeze, CancellationToken token) =>
        AgentFreezing.Describe(freeze
            ? await AgentFreezing.FreezeAsync(directory, agent, KeepFrozen, token).ConfigureAwait(false)
            : await AgentFreezing.UnfreezeAsync(directory, agent, token).ConfigureAwait(false));

    private async Task<string> FreezeEveryone(bool freeze, CancellationToken token)
    {
        var all = await AgentFreezing.AllAsync(directory, Project ?? string.Empty, freeze, KeepFrozen, token).ConfigureAwait(false);

        // Said apart from "nothing to do": a person freezing everything before a restart who is told the agents are already frozen
        // goes away, and a broker that could not be read froze nobody.
        if (all.Problem is { } problem)
        {
            return $"'{Project}' could not be read, so nothing was {(freeze ? "frozen" : "unfrozen")}. {problem}";
        }

        if (all.Agents.Count == 0)
        {
            return freeze
                ? $"Nothing to freeze: every agent in '{Project}' is already frozen, or none is registered."
                : $"Nothing to unfreeze: no agent in '{Project}' is frozen.";
        }

        // Counted by what was done, never by what was tried, and by what was done in this project: an agent frozen here whose
        // other project could not be reached is frozen here, and is named below with what happened in each.
        var here = all.Agents.Count(agent => agent.Projects.Any(
            done => done.Problem is null && string.Equals(done.Project, Project, StringComparison.Ordinal)));
        var incomplete = all.Agents.Count - all.Done;

        return $"{here} of {all.Agents.Count} {(all.Agents.Count == 1 ? "agent" : "agents")} {(freeze ? "frozen" : "unfrozen")} in '{Project}'."
            + (incomplete > 0
                ? $" {incomplete} {(incomplete == 1 ? "was" : "were")} not {(freeze ? "frozen" : "unfrozen")} in every project that serves {(incomplete == 1 ? "it" : "them")}: "
                  + string.Join(' ', all.Agents.Where(o => !o.Complete).Select(o => $"'{o.Agent.Value}': {AgentFreezing.Describe(o)}"))
                : string.Empty)
            // A freeze that ends no process is said agent by agent: a left process runs on until Kill, an editor's session stays
            // (review, lens 3 run B on fc26cb8a). Counted alone, the person was never told.
            + string.Concat(all.Agents
                .Where(o => o.Complete && o.Projects.Any(p => p.Process is FreezeProcess.LeftRunning or FreezeProcess.OpenOutside or FreezeProcess.NoNode))
                .Select(o => $" '{o.Agent.Value}': {AgentFreezing.Describe(o)}"))
            // One process serves every project an agent is in, so the act reached the others too: said by name, so the person
            // knows an agent frozen for another project's sake was released there (review, lens 2 run B on 44c8e1e6).
            + AgentFreezing.Elsewhere(all, Project ?? string.Empty, freeze);
    }

    // A refusal is said as the act that was refused, never as a read that failed, which is what the containment around every
    // call would otherwise call it.
    private static async Task<string> DescribeKill(AgentName agent, Task<bool> asked)
    {
        try
        {
            return Parley.Client.Rendering.StoppingWords.KillOutcome(agent.Value, await asked.ConfigureAwait(false));
        }
        catch (Grpc.Core.RpcException refused) when (refused.StatusCode == Grpc.Core.StatusCode.FailedPrecondition)
        {
            return Parley.Client.Rendering.StoppingWords.KillRefused(agent.Value, refused.Status.Detail);
        }
    }

    private static async Task<string> DescribeStop(AgentName agent, bool restart, Task<bool> asked) =>
        Parley.Client.Rendering.StoppingWords.Outcome(agent.Value, restart, await asked.ConfigureAwait(false));

    /// <summary>Writes the line where it takes effect, then records that a person accepted it.</summary>
    /// <param name="project">
    /// The project whose launch entry to write. Named rather than left null: enrolment writes
    /// <c>parley-&lt;project&gt;</c>, so asking for the unnamed key would refuse every enrolled repository.
    /// </param>
    /// <param name="broker">This project's broker.</param>
    /// <param name="agent">Whose line it is.</param>
    /// <param name="acceptance">
    /// What was agreed and how: a person's own line as composed, or an agent's answer as proposed or edited.
    /// </param>
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
    /// it: a stale workspace could otherwise hit a <em>different</em> agent's launch configuration.
    /// </para>
    /// </remarks>
    private static async Task<string> AcceptOwnership(
        ProjectSettings project,
        BrokerClient broker,
        AgentName agent,
        OwnershipAcceptance acceptance,
        CancellationToken token)
    {
        if (acceptance.Problem() is { } unacceptable)
        {
            return $"Not recorded: {unacceptable}";
        }

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
            .WriteAsync(known.Workspace, project.Name.Value, agent, acceptance.Statement, token)
            .ConfigureAwait(false);

        if (wrong is not null)
        {
            return $"Not recorded: {wrong}";
        }

        var record = await broker.RecordOwnershipAsync(agent, acceptance, token).ConfigureAwait(false);

        if (record.Recorded)
        {
            _ = await OwnershipLine
                .AfterRecordedAsync(known.Workspace, project.Name.Value, agent, acceptance.Statement, token)
                .ConfigureAwait(false);
        }

        return await DescribeOwnership(Task.FromResult(record)).ConfigureAwait(false);
    }

    /// <summary>Accepts an agent's waiting answer to Parley, as it wrote it or as a person edited it.</summary>
    /// <param name="project">The project whose launch entry to write.</param>
    /// <param name="broker">This project's broker.</param>
    /// <param name="agent">Whose answer.</param>
    /// <param name="typed">What the box held when the person confirmed.</param>
    /// <param name="token">Cancels the calls.</param>
    /// <returns>What to put in front of the person.</returns>
    /// <remarks>
    /// <para>
    /// <b>The answer is read again at the POST rather than trusted from the form</b>: the form is somebody's browser, and
    /// what decides whether the words are the agent's is a comparison with what the agent actually said, made here.
    /// Unchanged is <c>proposed</c>; anything else is <c>edited</c> — and surrounding whitespace is the form's, not a
    /// person's edit.
    /// </para>
    /// <para>
    /// The length recorded is the agent's own message, never the accepted line (the evidence is what agents write).
    /// Accepting settles Parley's question and closes its conversation, which the broker does as it records the line.
    /// </para>
    /// </remarks>
    private static async Task<string> AcceptAnswer(
        ProjectSettings project, BrokerClient broker, AgentName agent, string typed, CancellationToken token)
    {
        var report = await broker.FacilitationAsync(token).ConfigureAwait(false);

        if (report.Questions.FirstOrDefault(q => q.Agent == agent && q.State == QuestionState.Waiting)
            is not { Flattened: { } line, Answer: { } said } question)
        {
            return $"Nothing from '{agent.Value}' is waiting to be accepted.";
        }

        var statement = typed.Trim();

        if (statement.Length == 0)
        {
            statement = line;
        }

        var source = string.Equals(statement, line, StringComparison.Ordinal)
            ? OwnershipSource.Proposed
            : OwnershipSource.Edited;

        return await AcceptOwnership(
            project, broker, agent,
            new OwnershipAcceptance(statement, source, said.Length, question.Conversation),
            token).ConfigureAwait(false);
    }

    /// <summary>Has Parley ask every agent in a project again what it owns.</summary>
    /// <param name="broker">That project's broker, for the roster and the asks.</param>
    /// <param name="token">Cancels the reads and the asks.</param>
    /// <returns>What to put in front of the person.</returns>
    /// <remarks>
    /// <para>
    /// <b>Parley asks, so no seat is needed.</b>
    /// </para>
    /// <para>
    /// <b>Seats, retired agents and Parley itself are left out.</b> A seat is a person, a retired agent is not routed to,
    /// and Parley asks rather than being asked. An agent whose answer is still being decided is refused by the broker and
    /// named here, because asking again then would lose an answer nobody has looked at.
    /// </para>
    /// </remarks>
    private static async Task<string> AskEveryOwnership(BrokerClient broker, CancellationToken token)
    {
        var registry = await broker.RegistryAsync(token).ConfigureAwait(false);

        var asking = registry
            .Where(record => record.Status != AgentStatus.Retired)
            .Where(record => !string.Equals(record.Adapter, Seat.Adapter, StringComparison.Ordinal))
            .Where(record => !record.Facilitator)
            .Select(record => record.Name)
            .ToList();

        if (asking.Count == 0)
        {
            return "There is no agent in this project to ask. An agent appears once it has attached and "
                 + "registered, which is its own act as it starts.";
        }

        var asked = 0;
        var refused = new List<string>();

        foreach (var agent in asking)
        {
            if ((await broker.AskAgainAsync(agent, token).ConfigureAwait(false)).Done)
            {
                asked++;
            }
            else
            {
                refused.Add(agent.Value);
            }
        }

        return refused.Count == 0
            ? $"Parley asked {asked} agent(s) what they own. Their answers show in the ownership review."
            : $"Parley asked {asked} agent(s). Not asked, because an answer is still being decided: "
              + string.Join(", ", refused) + ".";
    }

    /// <summary>Says what became of an action on one of Parley's questions.</summary>
    /// <param name="action">The call.</param>
    /// <param name="done">What to say when it was done.</param>
    /// <returns>What to put in front of the person.</returns>
    private static async Task<string> DescribeAction(Task<FacilitationAction> action, string done)
    {
        var outcome = await action.ConfigureAwait(false);

        return outcome.Done ? done : $"Nothing was done: {outcome.Problem}";
    }

    /// <summary>Says what the Auto Accept switch now is.</summary>
    /// <param name="action">The call.</param>
    /// <param name="project">The project it belongs to.</param>
    /// <returns>What to put in front of the person.</returns>
    private static async Task<string> DescribeAutoAccept(Task<bool> action, ProjectSettings project) =>
        $"Auto Accept is {(await action.ConfigureAwait(false) ? "on" : "off")} for {project.Name.Value}.";

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

        var said = $"Removed {pruned.Removed} message(s).";

        if (pruned.HeldByFloor > 0)
        {
            said += $" {pruned.HeldByFloor} more were old enough and were kept, because they are on open work, not yet collected, or the request a result not yet collected carries back.";
        }

        said = pruned.Capped ? said + " It stopped at its own limit with more still to remove; run it again." : said;

        return pruned.Applied is { } policy ? said + " " + Parley.Client.Rendering.RetentionWords.Applied(policy) : said;
    }

    /// <summary>Retires, reinstates or purges, exactly as the terminal does: the name typed back, then the shared act.</summary>
    /// <param name="project">The project, whose launch entry is the one withdrawn.</param>
    /// <param name="broker">This project's broker.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="token">Cancels the calls and the file work.</param>
    /// <returns>What to put in front of the person.</returns>
    /// <remarks>
    /// <para>
    /// <b>The checks are made again here, at the POST, never trusted from the form.</b> The name must be typed exactly,
    /// a seat is refused, and a purge of an agent that still owes work needs the acceptance ticked against what it owes
    /// <em>now</em>: work may have arrived since the page was drawn.
    /// </para>
    /// <para>
    /// <b>The workspace comes from the registry</b>, which is what the agent said about itself.
    /// <see cref="Enrolment.WithdrawAsync(string, string, string?, string?, AgentIdentity, CancellationToken)"/> reads the name back out of the entry before removing it, so a stale
    /// workspace leaves a different agent's entry alone and says so.
    /// </para>
    /// </remarks>
    private async Task<string> Withdraw(
        ProjectSettings project, BrokerClient broker, AgentName agent, CancellationToken token)
    {
        if (PersonSeat.Is(agent))
        {
            return AgentWithdrawal.NotAnAgent;
        }

        if (AgentWithdrawal.TypedNameProblem(agent.Value, Typed) is { } wrong)
        {
            return wrong;
        }

        var action = Action!.ToLowerInvariant();

        if (action == "reinstate")
        {
            return await AgentWithdrawal.ReinstateAsync(broker, agent, token).ConfigureAwait(false);
        }

        if (action == "purge" && !AcceptOwed)
        {
            var owed = await OwedAsync(broker, agent, token).ConfigureAwait(false);

            if (owed is null or { Count: > 0 })
            {
                return AgentWithdrawal.Kept + " "
                    + (owed is { Count: > 0 }
                        ? AgentWithdrawal.OwedConsequence(agent.Value, owed)
                        : "What it owes could not be read, so it was not purged.");
            }
        }

        var registry = await broker.RegistryAsync(token).ConfigureAwait(false);
        var workspace = registry.FirstOrDefault(r => r.Name == agent)?.Workspace;
        var catalogue = WebCatalogue.Path(directory);

        return action == "purge"
            ? await AgentWithdrawal.PurgeAsync(broker, agent, workspace, project.Name.Value, catalogue, token).ConfigureAwait(false)
            : await AgentWithdrawal.RetireAsync(broker, agent, workspace, project.Name.Value, catalogue, token).ConfigureAwait(false);
    }

    /// <summary>Every exchange the agent is waited on in, or null when the broker could not be read.</summary>
    private static async Task<IReadOnlyList<WaitingEdge>?> OwedAsync(
        BrokerClient broker, AgentName agent, CancellationToken token)
    {
        var waiting = await BrokerDirectory.ReadAsync(async () =>
            await broker.WaitingAsync(token).ConfigureAwait(false)).ConfigureAwait(false);

        return waiting.Value?.Where(edge => edge.WaitedOn == agent).ToList();
    }
}
