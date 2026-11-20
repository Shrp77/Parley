using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;

namespace Parley.Web.Pages;

/// <summary>The graph of projects, agents and conversations, drawn as a fragment for the panel every page can open (spec 041).</summary>
/// <remarks>
/// <para>
/// <b>Reading it changes nothing</b>: it reads each project's registry and conversations, both operator reads, and takes no
/// delivery, so opening the panel never marks anything as seen. The collapsed panel asks for nothing; the console's script
/// fetches this only while it is open.
/// </para>
/// <para>
/// A fragment with no layout, fetched by the console's own script and put in place. Hold and Resume are links to the console's
/// own confirmation page, so the graph posts nothing itself.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class GraphModel(BrokerDirectory directory) : PageModel
{
    /// <summary>The most conversations one project's list is read to, across its pages.</summary>
    private const int MostRead = 400;

    /// <summary>The graph, once read.</summary>
    public AgentGraph Graph { get; private set; } = new([], [], [], 0, 0);

    /// <summary>When the brokers answered.</summary>
    public DateTimeOffset AsOf { get; private set; }

    /// <summary>The projects as the plain list shows them: their agents, and conversations by state.</summary>
    public IReadOnlyList<GraphInput> Inputs { get; private set; } = [];

    /// <summary>Reads every project this console serves.</summary>
    /// <returns>The fragment.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        var token = HttpContext.RequestAborted;
        var inputs = new List<GraphInput>();

        foreach (var project in directory.Projects)
        {
            var broker = new BrokerClient(directory.ClientFor(project));

            var roster = await BrokerDirectory.ReadAsync(async () =>
                await broker.RegistryAsync(token).ConfigureAwait(false)).ConfigureAwait(false);

            var conversations = new List<ConversationSummary>();
            string? problem = roster.Problem;
            string? next = null;

            do
            {
                var page = await BrokerDirectory.ReadAsync(async () =>
                    await broker.ConversationsAsync(pageToken: next, includeArchived: true, cancellationToken: token)
                        .ConfigureAwait(false)).ConfigureAwait(false);

                if (page.Value is null)
                {
                    problem ??= page.Problem;

                    break;
                }

                conversations.AddRange(page.Value.Items);
                next = page.Value.NextPageToken;
            }
            while (next is not null && conversations.Count < MostRead);

            inputs.Add(new GraphInput(
                project.Name.Value,
                roster.Value,
                conversations.Count > 0 || problem is null ? conversations : null,
                problem));
        }

        Inputs = inputs;
        Graph = AgentGraphBuilder.Build(inputs);
        AsOf = DateTimeOffset.Now;

        return Page();
    }
}
