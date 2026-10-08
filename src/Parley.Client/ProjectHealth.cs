using Parley.Core;

namespace Parley.Client;

/// <summary>What one project's broker said when the console asked what it speaks.</summary>
/// <param name="Speaks">The protocol version it reported, or null when it did not answer.</param>
/// <param name="Problem">Why it did not answer, when it did not.</param>
/// <param name="BrokerVersion">The build serving it, when it said.</param>
/// <param name="StoreProblems">How its store differs from the shape that build expects, when it said.</param>
/// <remarks>
/// Three states rather than two, and the third is the one worth carrying: a broker that answered
/// with a version this console was not built against is neither reachable-and-fine nor down, and
/// showing it as either would send somebody looking in the wrong place.
/// </remarks>
public sealed record ProjectHealth(
    string? Speaks,
    string? Problem,
    string? BrokerVersion = null,
    IReadOnlyList<string>? StoreProblems = null)
{
    /// <summary>Whether this project's broker answered at all.</summary>
    public bool Reachable => Problem is null;

    /// <summary>Whether it answered with a version this console was built against.</summary>
    public bool Agrees =>
        Reachable && string.Equals(Speaks, ProtocolVersion.Current, StringComparison.Ordinal);

    /// <summary>What is wrong with this project's deployment as this surface sees it, in words a person can act on.</summary>
    /// <param name="surfaceVersion">The build of the surface asking.</param>
    /// <returns>One line per problem, or nothing when the builds are one release and the store has the expected shape.</returns>
    /// <remarks>
    /// <para>
    /// Two things the protocol cannot say. A broker and a surface from different releases can both speak the same protocol
    /// and still disagree about everything the release changed; and a store made by an earlier build can read as current
    /// while lacking what has been added since. A broker that did not say either is not warned about: an older broker
    /// than this check cannot be asked, and "not said" is not "wrong".
    /// </para>
    /// <para>
    /// A broker that did not answer has nothing to compare, and says why in <see cref="Problem"/> instead.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Warnings(string surfaceVersion)
    {
        if (!Reachable)
        {
            return [];
        }

        var warnings = new List<string>();

        if (!string.IsNullOrEmpty(BrokerVersion) && !Compatibility.Agree(BrokerVersion, surfaceVersion))
        {
            warnings.Add(
                $"This broker is {BrokerVersion} and this program is {surfaceVersion}. They are from different releases, "
                + "so what they show each other may be wrong. Install the same release of every Parley tool.");
        }

        if (StoreProblems is { Count: > 0 } store)
        {
            warnings.Add(
                $"This project's store is not the shape this broker expects ({string.Join("; ", store.Take(3))}"
                + (store.Count > 3 ? $"; and {store.Count - 3} more" : string.Empty)
                + "). It was made by another build. Reset the project's store, or add what is missing, before relying on it.");
        }

        return warnings;
    }
}
