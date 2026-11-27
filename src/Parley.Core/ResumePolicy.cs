namespace Parley.Core;

/// <summary>
/// Whether a restart wakes agents for work that was outstanding when the broker stopped.
/// </summary>
/// <remarks>
/// <para>
/// <b>It decides only whether agents are woken, never whether the work survives</b> (SPEC.md §14).
/// Work stays open through any restart under every policy; what differs is whether anybody is started
/// to do it.
/// </para>
/// <para>
/// It exists because two restarts that are byte-identical to the broker carry opposite intent. An
/// operator watching two agents burn through a quota kills the process to stop them, and waking every
/// agent on restart overrides the control they reached for. An unattended machine takes a forced
/// update at the weekend, and leaving its work untouched wastes the window. Nothing in the restart
/// distinguishes those, so the broker does not try: the owner decided on 2026-11-18 that the second reading wins in the
/// broker the <c>serve</c> command runs (<see cref="Always"/>, on a node joining), and a broker object built without
/// options is <see cref="Never"/> (SPEC.md §14).
/// </para>
/// </remarks>
public enum ResumePolicy
{
    /// <summary>Nobody is woken for outstanding work. It stays open and is flagged on the ordinary schedule.</summary>
    /// <remarks>
    /// The default of a broker object, not of the <c>serve</c> command, which uses <see cref="Always"/>.
    /// The durable way to stop work is to close its conversation.
    /// </remarks>
    Never = 0,

    /// <summary>Agents with outstanding work are woken, subject to the resume gate.</summary>
    /// <remarks>The sensible default for unattended use, where nobody is present to wake anything by hand.</remarks>
    Always,

    /// <summary>The outstanding work is shown to the person, and agents are woken only for what they confirm.</summary>
    /// <remarks>
    /// The person confirms by starting the agent (§10.2); until then the work is flagged, which is the
    /// safe way for a deployment with nobody watching to degrade.
    /// </remarks>
    Ask
}
