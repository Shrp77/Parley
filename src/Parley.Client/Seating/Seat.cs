using System.Globalization;
using Parley.Core;

namespace Parley.Client.Seating;

/// <summary>
/// The person, as a project's broker names them, and the identity they speak through.
/// </summary>
/// <param name="Name">The project's person seat, read from its registry: <c>human-user@&lt;project&gt;</c>.</param>
/// <remarks>
/// <para>
/// <b>The name comes from the broker and never from the surface</b> (specs/023, FR-001a). Until 023 a person typed a
/// name and this record added the surface's own machine to it, so a person reaching a broker through a tunnel from a
/// laptop became a second, differently named person in the same project. Now the broker provides the one person seat
/// in every project it serves (SPEC.md §12.3), and a surface on any machine speaks as that row. Nothing about the
/// surface — its machine, its user, anything typed — can change who the person is.
/// </para>
/// <para>
/// SPEC.md §11.2 admits a human as a responder and §11.3 gives them an identity, <c>human:&lt;name&gt;</c>. The name is
/// the role, <c>human-user</c>, for everyone using a project: a deliberate choice (023's D4) for a protocol used by one
/// person running many agents.
/// </para>
/// </remarks>
public sealed record Seat(AgentName Name)
{
    /// <summary>What a person's seat attaches through.</summary>
    /// <remarks>
    /// Load-bearing: it is how a registry row is known to be the person rather than a process, and a person may always
    /// start a new conversation (SPEC.md principle 2.10) where an agent may not. The same value as
    /// <see cref="PersonSeat.Adapter"/>, which is where the broker reads it.
    /// </remarks>
    public const string Adapter = PersonSeat.Adapter;

    /// <summary>Who this is, without the project: the role, <c>human-user</c>.</summary>
    public string Person => Name.Role;

    /// <summary>The identity SPEC.md §11.3 records against everything they say.</summary>
    public string Responder =>
        string.Create(CultureInfo.InvariantCulture, $"human:{Name.Role}");

    /// <summary>The person seat in a project's registry, or <see langword="null"/> when it has none.</summary>
    /// <param name="registry">The registry as the broker reported it.</param>
    /// <returns>The seat.</returns>
    /// <remarks>
    /// <b>Found by role, as the broker finds it.</b> A registry can still hold rows taken under typed names before 023,
    /// under the same adapter; the role is the one fact only the person seat has. A registry with no person row is a
    /// broker older than 0.36.0, and the caller says so rather than inventing a name.
    /// </remarks>
    public static Seat? In(IEnumerable<AgentRecord> registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return registry.FirstOrDefault(row => PersonSeat.Is(row.Name)) is { } row ? new Seat(row.Name) : null;
    }
}
