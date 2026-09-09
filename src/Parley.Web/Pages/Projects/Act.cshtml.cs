using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;
using Parley.Client;

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
public sealed class ActModel(BrokerDirectory directory) : PageModel
{
    /// <summary>Which project.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>What to do: retire, reinstate, purge, forceconclude or instruct.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Action { get; set; }

    /// <summary>Who or what it is about.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Subject { get; set; }

    /// <summary>The operator's words, when instructing.</summary>
    [BindProperty]
    public string? Text { get; set; }

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

            "forceconclude" => ($"Conversation '{Subject}' will be closed over its participants' "
                              + "votes. The log records that a person closed it.", false),

            "instruct" => ($"'{Subject}' will be given your words as a turn. It may decline, which "
                         + "is not a failure. The instruction is recorded as yours.", false),

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

        // The subject arrives in a URL, so it is whatever was typed there. Four of these five
        // actions need it to be an agent name, and the parse is done once, here, rather than
        // inside the switch: AgentName.Create throws on anything malformed, and an unhandled
        // throw out of a page handler is a stack trace served to whatever asked for it.
        var named = AgentName.TryCreate(Subject, out var agent);

        var read = await BrokerDirectory.ReadAsync(async () =>
            await (Action?.ToLowerInvariant() switch
            {
                // forceconclude takes a conversation identifier, which is opaque and chosen by an
                // agent (§5.0). There is nothing to validate it against, so it is passed through.
                "forceconclude" => Describe(broker.ForceConcludeAsync(Subject, token)),

                _ when !named => Task.FromResult(
                    $"'{Subject}' is not a name an agent can have, so there is nothing to act on."),

                "retire" => Describe(broker.RetireAsync(agent, token)),

                "reinstate" => Describe(broker.ReinstateAsync(agent, token)),

                "purge" => DescribePurge(broker.PurgeAsync(agent, token)),

                "instruct" => DescribeInstruction(
                    broker.InstructAsync(agent, Text ?? string.Empty, token)),

                _ => Task.FromResult("This is not something the console does.")
            }).ConfigureAwait(false)).ConfigureAwait(false);

        Outcome = read.Value ?? read.Problem;

        return Page();
    }

    private static async Task<string> Describe(Task<bool> action) =>
        await action.ConfigureAwait(false) ? "Done." : "Nothing changed.";

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
                "The machine that holds this agent is not connected, so nothing was delivered.",
            InstructionOutcome.Failed => $"The turn did not complete. {result.Reply}",

            // The ordinary answer for any real piece of work, and it must not read as a fault.
            _ => "Given to the agent, and the turn is still running. Its answer will appear under "
                 + "What you have told it on the agent's page — you can close this."
        };
    }
}
