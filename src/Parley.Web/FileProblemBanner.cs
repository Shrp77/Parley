using Parley.Client;
using Parley.Client.Files;

namespace Parley.Web;

/// <summary>
/// How many file problems wait in the projects a page is about, for the banner the frame draws on every page (SPEC.md
/// §9.5, §10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wherever the person is.</b> A page inside a project counts that project; a page about the whole deployment counts
/// every project, so a quarantined file is never somewhere the person would have to go looking for it.
/// </para>
/// <para>
/// <b>Together, and unknown left out.</b> Every broker is asked at once, so one that is down costs one timeout rather
/// than one for each, and a project whose broker could not say shows nothing rather than a count it does not have.
/// </para>
/// </remarks>
internal static class FileProblemBanner
{
    /// <summary>The page the banner links to.</summary>
    internal const string Page = "/Projects/FileProblems";

    /// <summary>The projects with problems waiting, and how many each has.</summary>
    /// <param name="directory">The projects and their brokers.</param>
    /// <param name="project">The project the page is about, or empty for the whole deployment.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Each project with one or more waiting, in name order.</returns>
    internal static async Task<IReadOnlyList<(string Project, int Waiting)>> CountAsync(
        BrokerDirectory directory, string? project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);

        IReadOnlyList<Parley.Core.ProjectSettings> asked = string.IsNullOrEmpty(project)
            ? directory.Projects
            : directory.Find(project) is { } one ? [one] : [];

        // Each project read on its own guard, the client built inside it: an endpoint that cannot be dialled throws as the
        // client is made, and one project's must not take the page down for every other.
        var counts = await Task.WhenAll(asked.Select(async settings =>
            (Project: settings.Name.Value,
             Waiting: (await BrokerDirectory.ReadAsync(async () =>
                     await FileProblemSaid.WaitingAsync(new BrokerClient(directory.ClientFor(settings)), cancellationToken)
                         .ConfigureAwait(false))
                 .ConfigureAwait(false)).Value))).ConfigureAwait(false);

        return [.. counts.Where(c => c.Waiting is > 0).Select(c => (c.Project, c.Waiting!.Value))];
    }
}
