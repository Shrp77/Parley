using Parley.Core;

namespace Parley.Client;

/// <summary>What one project's broker said when the console asked what it speaks.</summary>
/// <param name="Speaks">The protocol version it reported, or null when it did not answer.</param>
/// <param name="Problem">Why it did not answer, when it did not.</param>
/// <remarks>
/// Three states rather than two, and the third is the one worth carrying: a broker that answered
/// with a version this console was not built against is neither reachable-and-fine nor down, and
/// showing it as either would send somebody looking in the wrong place.
/// </remarks>
public sealed record ProjectHealth(string? Speaks, string? Problem)
{
    /// <summary>Whether this project's broker answered at all.</summary>
    public bool Reachable => Problem is null;

    /// <summary>Whether it answered with a version this console was built against.</summary>
    public bool Agrees =>
        Reachable && string.Equals(Speaks, ProtocolVersion.Current, StringComparison.Ordinal);
}
