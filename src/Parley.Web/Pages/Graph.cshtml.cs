using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Client.Views;
using Parley.Core;

namespace Parley.Web.Pages;

/// <summary>The picture of what is being worked on now, drawn as a fragment for the panel every page can open (spec 041,
/// reworked by feature 042).</summary>
/// <remarks>
/// <para>
/// <b>Reading it changes nothing</b>: it reads each project's registry, its wait-for graph and the conversations its working
/// agents owe answers in, all operator reads, and takes no delivery, so opening the panel never marks anything as seen. The
/// collapsed panel asks for nothing; the console's script fetches this only while it is open.
/// </para>
/// <para>
/// <b>Only working agents cost a read.</b> What an agent owes is read only for an agent at work, and no more than
/// <see cref="MostConversations"/> conversations a project, so a deployment of idle agents costs one registry read a project.
/// </para>
/// <para>
/// A fragment with no layout, fetched by the console's own script and put in place. The filter's choices are sent back as
/// <c>project</c> and <c>agent</c>; the panel's script keeps them. Freeze and Unfreeze are links to the console's own
/// confirmation page, so the picture posts nothing itself.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class GraphModel(BrokerDirectory directory) : PageModel
{
    /// <summary>The most conversations one project's working agents are read in.</summary>
    private const int MostConversations = 30;

    /// <summary>The most exchanges one project's working agents are read for.</summary>
    private const int MostExchanges = 100;

    /// <summary>The picture, once read.</summary>
    public WorkPicture Picture { get; private set; } = WorkNow.Build([], DateTimeOffset.Now);

    /// <summary>When the brokers answered.</summary>
    public DateTimeOffset AsOf { get; private set; }

    /// <summary>The projects chosen in the filter.</summary>
    public IReadOnlyList<string> ChosenProjects { get; private set; } = [];

    /// <summary>The agents chosen in the filter.</summary>
    public IReadOnlyList<string> ChosenAgents { get; private set; } = [];

    /// <summary>Reads every project this console serves.</summary>
    /// <param name="project">The projects chosen in the filter; none is every project.</param>
    /// <param name="agent">The agents chosen in the filter; none is every agent.</param>
    /// <param name="narrow">Whether the panel is as narrow as a phone, so the picture is drawn in one column.</param>
    /// <returns>The fragment.</returns>
    public async Task<IActionResult> OnGetAsync([FromQuery] string[]? project, [FromQuery] string[]? agent, [FromQuery] bool narrow = false)
    {
        var token = HttpContext.RequestAborted;
        var now = DateTimeOffset.UtcNow;
        var served = directory.Projects.Select(p => p.Name.Value).ToHashSet(StringComparer.Ordinal);

        ChosenProjects = [.. (project ?? []).Where(served.Contains).Distinct(StringComparer.Ordinal)];
        // Held to the name grammar, so a chip never draws a character that could reorder or hide its text.
        ChosenAgents = [.. (agent ?? []).Where(a => AgentName.TryCreate(a, out _)).Distinct(StringComparer.Ordinal).Take(200)];

        var inputs = new List<WorkInput>();

        foreach (var served1 in directory.Projects)
        {
            var name = served1.Name.Value;
            var broker = new BrokerClient(directory.ClientFor(served1));

            var roster = await BrokerDirectory.ReadAsync(async () =>
                await broker.RegistryAsync(token).ConfigureAwait(false)).ConfigureAwait(false);

            var owed = new List<OwedWork>();
            string? problem = roster.Problem;

            var busy = (roster.Value ?? [])
                .Where(a => WorkNow.IsAtWork(a, now))
                .Where(a => ChosenAgents.Count == 0 || ChosenAgents.Contains(a.Name.Value))
                .Select(a => a.Name.Value)
                .ToHashSet(StringComparer.Ordinal);

            if (busy.Count > 0 && (ChosenProjects.Count == 0 || ChosenProjects.Contains(name)))
            {
                problem ??= await ReadOwedAsync(broker, busy, owed, token).ConfigureAwait(false);
            }

            inputs.Add(new WorkInput(name, roster.Value, owed, problem));
        }

        Picture = WorkNow.Build(inputs, now, ChosenProjects, ChosenAgents, narrow);
        AsOf = DateTimeOffset.Now;

        return Page();
    }

    /// <summary>Reads what the busy agents owe in one project: the exchanges waiting on them, and for each, whether they
    /// have collected the message they owe an answer to.</summary>
    /// <returns>Why it could not be read in full, or null.</returns>
    private static async Task<string?> ReadOwedAsync(BrokerClient broker, HashSet<string> busy, List<OwedWork> owed, CancellationToken token)
    {
        var waiting = await BrokerDirectory.ReadAsync(async () =>
            await broker.WaitingAsync(token).ConfigureAwait(false)).ConfigureAwait(false);

        if (waiting.Value is null)
        {
            return waiting.Problem;
        }

        var exchanges = waiting.Value
            .Where(e => busy.Contains(e.WaitedOn.Value))
            .Select(e => (e.ExchangeId, Owner: e.WaitedOn))
            .Distinct()
            .OrderBy(e => e.ExchangeId)
            .ToList();

        // The oldest first, and no more than a bounded number: a peer can send an agent any number of requests, and each
        // refresh of every open panel would otherwise read them all.
        string? problem = exchanges.Count > MostExchanges
            ? $"Only the oldest {MostExchanges} of {exchanges.Count} exchanges its working agents owe are shown."
            : null;
        var byConversation = new Dictionary<string, List<(long ExchangeId, AgentName Owner)>>(StringComparer.Ordinal);

        foreach (var exchange in exchanges.Take(MostExchanges))
        {
            var located = await BrokerDirectory.ReadAsync(async () =>
                await broker.LocateExchangeAsync(exchange.ExchangeId, token).ConfigureAwait(false)).ConfigureAwait(false);

            if (located.Value is not { } conversation)
            {
                // Work left out of the picture is said, never dropped in silence.
                problem ??= located.Problem;

                continue;
            }

            if (!byConversation.TryGetValue(conversation, out var list))
            {
                if (byConversation.Count >= MostConversations)
                {
                    problem ??= $"Only the first {MostConversations} conversations its working agents owe work in are shown.";

                    continue;
                }

                byConversation[conversation] = list = [];
            }

            list.Add(exchange);
        }

        foreach (var (conversation, list) in byConversation)
        {
            var export = await BrokerDirectory.ReadAsync(async () =>
                await broker.ExportAsync(conversation, token).ConfigureAwait(false)).ConfigureAwait(false);

            if (export.Value is null)
            {
                problem ??= export.Problem;

                continue;
            }

            foreach (var (exchangeId, owner) in list)
            {
                var messages = export.Value.Messages.Where(m => m.ExchangeId == exchangeId).ToList();
                var last = messages.Where(m => m.To == owner).MaxBy(m => m.Id);

                if (last is null)
                {
                    continue;
                }

                var request = messages.Where(m => m.Type == MessageType.Request).MinBy(m => m.Id);
                var ask = Payload.Read(request?.PayloadJson).Said;

                // The latest message to it is what it has or has not collected. When that is a later message than the
                // Request (an answer to its own question, say), it took the work before and a reply is what waits.
                var reply = request is not null && last.Id != request.Id;

                owed.Add(new OwedWork(
                    exchangeId, owner, conversation, ask, request?.CreatedUtc ?? last.CreatedUtc, last.DeliveredUtc is not null, reply));
            }
        }

        return problem;
    }
}
