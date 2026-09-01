using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;

namespace Parley.Web.Pages;

/// <summary>
/// The projects this machine is configured for.
/// </summary>
/// <remarks>
/// The only page that is not scoped to one project, because it is the page you choose one from.
/// Everything below it names the project it belongs to: SPEC.md §4.1 gives each its own store and
/// endpoint, so two projects may each hold an agent called <c>orders</c> and a view that merged
/// them would show colliding names with nothing to say which environment each belonged to.
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class IndexModel(BrokerDirectory directory) : PageModel
{
    /// <summary>The projects named in this machine's manifest.</summary>
    public IReadOnlyList<ProjectSettings> Projects { get; private set; } = [];

    /// <summary>What each project's broker said when asked, keyed by project name.</summary>
    public IReadOnlyDictionary<string, ProjectHealth> Health { get; private set; } =
        new Dictionary<string, ProjectHealth>(StringComparer.Ordinal);

    /// <summary>Why the manifest could not be read, when it could not.</summary>
    public string? ManifestProblem { get; private set; }

    /// <summary>When this page was built.</summary>
    /// <remarks>
    /// A page that refreshes itself and does not say when it last did leaves a reader unable to
    /// tell a live tab from one left open since yesterday — and the two look identical, which is
    /// the whole problem. Said on every list view for that reason, including this one, where what
    /// goes stale is the manifest rather than a broker's answer.
    /// </remarks>
    public DateTimeOffset AsOf { get; private set; }

    /// <summary>The protocol version this console was built against.</summary>
    public static string ConsoleSpeaks => ProtocolVersion.Current;

    /// <summary>Reads the manifest and asks each project's broker what it speaks.</summary>
    /// <returns>The page.</returns>
    /// <remarks>
    /// <para>
    /// <c>implements</c> is asked first and asked here, before any page offers a read that could
    /// be misread. A contract whose version cannot be discovered is one nobody can rely on, and
    /// without it a broker of the wrong version is indistinguishable from one that is switched
    /// off — the operator gets an empty roster in both cases and no reason to look further.
    /// </para>
    /// <para>
    /// A mismatch is named rather than refused. The console still starts, still lists the
    /// project, and still lets its pages be opened: what a version disagreement means for any
    /// particular read is not knowable from the numbers, and a console that locked the operator
    /// out would remove the view at exactly the moment it is most needed. Both versions are
    /// named, because "incompatible" without the two numbers cannot be acted on.
    /// </para>
    /// <para>
    /// Each project is asked independently through <see cref="BrokerDirectory.ReadAsync{T}"/>, so
    /// one broker being down leaves every other row on the page intact — and all of them are
    /// asked at once. In sequence, the cost of a page is the sum of the timeouts rather than the
    /// longest of them: a machine with a dozen projects and two of them down would take
    /// twenty-odd seconds to say so, which is a page nobody waits for and therefore a fact nobody
    /// reads. Concurrently it is one timeout however many are down.
    /// </para>
    /// </remarks>
    public async Task OnGetAsync()
    {
        Projects = directory.Projects;
        ManifestProblem = directory.ManifestProblem;

        var asked = Projects.Select(async project =>
        {
            var read = await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project))
                    .ImplementsAsync(HttpContext.RequestAborted).ConfigureAwait(false))
                .ConfigureAwait(false);

            return (project.Name.Value, Health: new ProjectHealth(read.Value, read.Problem));
        });

        var answers = await Task.WhenAll(asked).ConfigureAwait(false);
        var health = new Dictionary<string, ProjectHealth>(StringComparer.Ordinal);

        foreach (var (name, said) in answers)
        {
            // Assigned rather than added. A manifest naming one project twice is a manifest
            // problem the reader is told about elsewhere; it must not be a page that throws,
            // which is what building this by ToDictionary would do.
            health[name] = said;
        }

        Health = health;
        AsOf = DateTimeOffset.Now;
    }
}
