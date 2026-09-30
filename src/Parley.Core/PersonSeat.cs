namespace Parley.Core;

/// <summary>
/// The person seat: the project's one participant that is a person (SPEC.md §3, §12.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Having a seat is not being in it.</b> The broker writes <c>human-user@&lt;project&gt;</c> when it opens a store
/// and never releases it, so an agent that needs a person's decision always has somebody to ask (principle 2.6),
/// however long the person has been away. Whether somebody is at a screen is a separate fact — <c>here</c> or
/// <c>away</c> (§14) — read from whether a surface is attached, and it never removes the seat.
/// </para>
/// <para>
/// <b>The same shape as <see cref="Facilitation"/>, on purpose.</b> They are the two participants the broker provides
/// rather than a launch, both are named for their project rather than a machine, and storage records no machine for
/// either. Two types written alike are how a reader sees they are one kind of thing.
/// </para>
/// <para>
/// <b>The reservation is narrower than it sounds</b>, and §12.3 says so. Storage refuses the role under any other
/// adapter, and enrolment and attaching as an agent refuse it with <see cref="Reserved"/>. But the row exists before
/// anything attaches, so an attach under its name fires no storage rule: what refuses a claimant is code comparing
/// the adapter the caller reports. What storage guarantees whoever attaches is that the seat carries no ownership
/// line.
/// </para>
/// <para>
/// <b>Everyone using a project is the one <c>human-user</c></b> (specs/023 D4) — a deliberate choice for a protocol
/// used by one person running many agents. <c>user</c> was not chosen because an agent may legitimately be called
/// that.
/// </para>
/// </remarks>
public static class PersonSeat
{
    /// <summary>The reserved role. Storage refuses it to any adapter but <see cref="Adapter"/>.</summary>
    public const string Role = "human-user";

    /// <summary>The adapter a person's seat attaches through, and which a person is recognised by.</summary>
    /// <remarks>
    /// Recognising a person by adapter matters in one place in the protocol: a person may always start a new
    /// conversation (principle 2.10), where an agent holding a Request may not.
    /// </remarks>
    public const string Adapter = "parley-console";

    /// <summary>The person seat's name in <paramref name="project"/>.</summary>
    /// <param name="project">The project the broker serves.</param>
    /// <returns><c>human-user@project</c>.</returns>
    public static AgentName On(ProjectName project) => AgentName.Create(Role + "@" + project.Value);

    /// <summary>Whether a name is the person seat's, in any project.</summary>
    /// <param name="agent">The name.</param>
    /// <returns>Whether its role is the reserved one.</returns>
    public static bool Is(AgentName agent) => string.Equals(agent.Role, Role, StringComparison.Ordinal);

    /// <summary>Whether a role is the reserved one, so a door can refuse it before storage does.</summary>
    /// <param name="role">The role, as typed or derived.</param>
    /// <returns>Whether it is reserved.</returns>
    public static bool IsReserved(string? role) => string.Equals(role, Role, StringComparison.Ordinal);

    /// <summary>What a door says when it refuses the reserved role.</summary>
    public const string Reserved =
        "'" + Role + "' is the person's seat in every project, so no agent can be called that. Choose another name.";
}
