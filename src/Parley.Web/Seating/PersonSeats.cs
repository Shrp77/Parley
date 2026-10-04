using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;

namespace Parley.Web.Seating;

/// <summary>
/// The person's seat in every project this console shows, taken without a word from them (specs/023).
/// </summary>
/// <remarks>
/// <para>
/// <b>What replaced the Join page and the seat cookie.</b> A person used to type a name, which a cookie remembered and a
/// page turned into a seat — and the frame then had to reconcile the two whenever they parted company, which they did on
/// every broker restart and twenty minutes after anybody walked away. The broker now provides the person seat in every
/// project, so there is nothing to type and nothing to remember: each page takes the seat the registry names, if nothing
/// holds it already.
/// </para>
/// <para>
/// <b>One implementation for the frame and the pages</b>, because a seat taken by the frame and looked up by a page by
/// two different routes is how the two come to disagree about whether the person is here.
/// </para>
/// </remarks>
internal static class PersonSeats
{
    /// <summary>Takes the person's seat in every project that does not hold one now, and marks the person here.</summary>
    /// <param name="seats">The book.</param>
    /// <param name="directory">The projects, and a client for each.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>When every project has been tried.</returns>
    /// <remarks>
    /// A page render is evidence the person is at the console, which is the whole of what keeps them marked here:
    /// SPEC.md §4.0 has no heartbeat. Each project is tried independently; one broker that does not answer leaves that
    /// project without a seat and every other project with one.
    /// </remarks>
    /// <param name="person">
    /// Whether a person caused this render. False for a page reloading itself on a timer, which keeps the seat's stream
    /// but is no evidence anybody is there: SPEC.md §14 has <c>here</c> mean in use, and a tab left open on a page that
    /// reloads every thirty seconds would otherwise read <c>here</c> for ever (the audit before the gate resumed).
    /// </param>
    /// <returns>When every project has been tried.</returns>
    internal static async Task EnsureAsync(
        SeatBook seats, BrokerDirectory directory, CancellationToken cancellationToken, bool person = true)
    {
        ArgumentNullException.ThrowIfNull(seats);
        ArgumentNullException.ThrowIfNull(directory);

        foreach (var project in directory.Projects)
        {
            await EnsureAsync(seats, directory, project, cancellationToken).ConfigureAwait(false);
        }

        if (person)
        {
            seats.Touch(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>The query parameter a page's own timed reload carries, so the render can tell it from a person.</summary>
    internal const string Reloaded = "reloaded";

    /// <summary>The person's seat in one project, taking it first if nothing holds it now.</summary>
    /// <param name="seats">The book.</param>
    /// <param name="directory">The projects, and a client for each.</param>
    /// <param name="project">The project.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The seat, or the reason there is none.</returns>
    internal static Task<SeatOutcome> EnsureAsync(
        SeatBook seats, BrokerDirectory directory, ProjectSettings project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seats);
        ArgumentNullException.ThrowIfNull(directory);

        return seats.EnsureAsync(
            project,
            async token => (await BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(directory.ClientFor(project))
                    .RegistryAsync(token).ConfigureAwait(false)).ConfigureAwait(false)).Value,
            cancellationToken);
    }
}
