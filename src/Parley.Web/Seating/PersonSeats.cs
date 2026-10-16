using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;

namespace Parley.Web.Seating;

/// <summary>
/// The person's seat in every project this console shows, taken without a word from them.
/// </summary>
/// <remarks>
/// <para>
/// <b>No name is typed and nothing is remembered.</b> The broker provides the person seat in every
/// project, so each page takes the seat the registry names, if nothing holds it already.
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
    /// <param name="person">
    /// Whether a person caused this render. False for a page reloading itself on a timer, which keeps the seat's stream
    /// but is no evidence anybody is there: SPEC.md §14 has <c>here</c> mean in use, and a tab left open on a page that
    /// reloads every thirty seconds would otherwise read <c>here</c> for ever.
    /// </param>
    /// <returns>When every project has been tried.</returns>
    /// <remarks>
    /// A page render is evidence the person is at the console, which is the whole of what keeps them marked here:
    /// SPEC.md §4.0 has no heartbeat. Each project is tried independently; one broker that does not answer leaves that
    /// project without a seat and every other project with one.
    /// </remarks>
    internal static async Task EnsureAsync(
        SeatBook seats, BrokerDirectory directory, CancellationToken cancellationToken, bool person = true)
    {
        ArgumentNullException.ThrowIfNull(seats);
        ArgumentNullException.ThrowIfNull(directory);

        // Together, so a broker that is down costs one timeout and not one for each project that is.
        await Task.WhenAll(directory.Projects.Select(project => EnsureAsync(seats, directory, project, cancellationToken)))
            .ConfigureAwait(false);

        if (person)
        {
            seats.Touch(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Takes the seats and counts what is waiting, both at once.</summary>
    /// <param name="seats">The book.</param>
    /// <param name="directory">The projects, and a client for each.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <param name="person">Whether a person caused this, as for <c>EnsureAsync</c>.</param>
    /// <returns>How much is waiting for the person, and whether every project answered.</returns>
    /// <remarks>
    /// The frame does this while a page is drawn, and pages that do not need an answer to draw ask for it afterwards
    /// instead. Mail waits against the name, not the stream, so the count does not need the seat taken first.
    /// </remarks>
    internal static async Task<(int Waiting, bool Certain)> PresenceAsync(
        SeatBook seats, BrokerDirectory directory, CancellationToken cancellationToken, bool person = true)
    {
        ArgumentNullException.ThrowIfNull(seats);
        ArgumentNullException.ThrowIfNull(directory);

        var taking = EnsureAsync(seats, directory, cancellationToken, person);
        var counting = seats.WaitingAsync(directory.Projects, cancellationToken);

        await Task.WhenAll(taking, counting).ConfigureAwait(false);

        return await counting.ConfigureAwait(false);
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
