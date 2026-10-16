using Parley.Core;

namespace Parley.Client.Seating;

/// <summary>
/// The person, as a project's broker names them, and the identity they speak through.
/// </summary>
/// <param name="Name">The project's person seat, read from its registry: <c>human-user@&lt;project&gt;</c>.</param>
/// <remarks>
/// <para>
/// <b>The name comes from the broker and never from the surface</b>. The broker provides the one person seat
/// in every project it serves (SPEC.md §12.3), and a surface on any machine speaks as that row. Nothing about the
/// surface — its machine, its user, anything typed — can change who the person is.
/// </para>
/// <para>
/// The responder recorded against what the person says (SPEC.md §11.3) is the seat's own name, <c>human-user</c>, like any
/// other participant's. It is the role for everyone using a project: a deliberate choice for a protocol used by one
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
    public string Responder => Name.Role;

    /// <summary>The person seat in a project's registry, or <see langword="null"/> when it has none.</summary>
    /// <param name="registry">The registry as the broker reported it.</param>
    /// <returns>The seat.</returns>
    /// <remarks>
    /// <b>Found by role, as the broker finds it.</b> The role is the one fact only the person seat has. A registry
    /// with no person row yields null, and the caller says so rather than inventing a name.
    /// </remarks>
    public static Seat? In(IEnumerable<AgentRecord> registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return registry.FirstOrDefault(row => PersonSeat.Is(row.Name)) is { } row ? new Seat(row.Name) : null;
    }
}
