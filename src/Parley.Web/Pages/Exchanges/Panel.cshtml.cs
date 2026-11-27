using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Client.Seating;

namespace Parley.Web.Pages.Exchanges;

/// <summary>One exchange, drawn as a fragment for the panel a reference to it opens (spec 036, FR-020).</summary>
/// <remarks>
/// <para>
/// <b>Reading it changes nothing</b> (FR-021): it locates the exchange and exports its conversation, both operator reads.
/// No delivery is taken, so opening a reference in a message never marks anything as seen.
/// </para>
/// <para>
/// A fragment with no layout, fetched by the console's own script and put under the line the reference is on. Its messages
/// are drawn through the one message view, so a reference inside the panel opens another panel.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class PanelModel(BrokerDirectory directory) : PageModel
{
    /// <summary>The project the reference was read in.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>The exchange's number.</summary>
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    /// <summary>The exchange, once read.</summary>
    public ExchangeDetail? Detail { get; private set; }

    /// <summary>How it ended, when it is closed.</summary>
    public ExchangeEnding? Ended { get; private set; }

    /// <summary>Why it could not be read, when it could not.</summary>
    public string? Problem { get; private set; }

    /// <summary>Reads the exchange.</summary>
    /// <returns>The fragment, or 404 for a project this console does not know.</returns>
    public async Task<IActionResult> OnGetAsync()
    {
        if (directory.Find(Project) is not { } project)
        {
            return NotFound();
        }

        Project = project.Name.Value;

        if (Id <= 0)
        {
            Detail = new ExchangeDetail(Id, null, [], null, null);
            return Page();
        }

        var read = await BrokerDirectory
            .ReadAsync(async () =>
            {
                var broker = new BrokerClient(directory.ClientFor(project));
                var conversation = await broker.LocateExchangeAsync(Id, HttpContext.RequestAborted).ConfigureAwait(false);

                return await ExchangeDetail.ReadAsync(broker, Id, conversation, HttpContext.RequestAborted).ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        Detail = read.Value;
        Problem = read.Problem;
        Ended = Detail?.Ending();

        return Page();
    }
}
