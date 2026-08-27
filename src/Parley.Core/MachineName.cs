namespace Parley.Core;

/// <summary>
/// What this machine is called, for the machine half of an agent's name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Detected, never configured</b>, per SPEC.md §4.1.1. A machine that can be told what it is
/// called can be told the wrong thing, and two boxes both answering to <c>server-b</c> is a failure
/// that surfaces as messages arriving somewhere nobody expected — long after the mistake, and
/// with nothing in the registry that looks wrong.
/// </para>
/// <para>
/// It lives in Core rather than beside the node because both ends need it and neither may
/// disagree with the other: the adapter builds its own name from it at launch, and the node
/// builds the same name when it decides whether an agent belongs to this machine. Two
/// detections that normalised differently would produce an agent nothing could start.
/// </para>
/// </remarks>
public static class MachineName
{
    /// <summary>The variable that overrides detection.</summary>
    /// <remarks>
    /// <para>
    /// §4.1.1 says the machine is detected and never configured, and the reason is sound: a
    /// machine that can be told what it is called can be told the wrong thing, and two boxes
    /// answering to one name is a failure that surfaces long after the mistake.
    /// </para>
    /// <para>
    /// This exists anyway, because detection alone breaks a case that is not exotic. A container
    /// is given a random host name at start, so an agent in one would take a new identity on
    /// every restart — abandoning its registration, its open exchanges and every peer's record of
    /// dealing with it, silently, as a routine consequence of being restarted. That is worse than
    /// the risk the rule is guarding against.
    /// </para>
    /// <para>
    /// The guard that actually holds is the collision check at registration: two machines
    /// claiming one name are refused and told which node holds it. This override does not weaken
    /// that. What it removes is the pretence that a host name is always a stable identity.
    /// </para>
    /// </remarks>
    public const string Variable = "PARLEY_MACHINE";

    /// <summary>This machine's name, in the form <see cref="AgentName"/> admits.</summary>
    public static string Current { get; } =
        Sanitise(Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } named
            ? named
            : Environment.MachineName);

    /// <summary>
    /// Reduces a host name to something the name grammar admits.
    /// </summary>
    /// <param name="raw">What the operating system reports.</param>
    /// <returns>A usable machine half.</returns>
    /// <remarks>
    /// <para>
    /// Host names carry characters the grammar of §4.1.1 does not: an underscore is legal in
    /// NetBIOS and common on Windows, and a host name may be upper case anywhere. Replacing
    /// rather than refusing, because a machine cannot choose its own name and refusing to run on
    /// it would punish the wrong party — an operator cannot rename a domain controller to please
    /// a messaging protocol.
    /// </para>
    /// <para>
    /// Lossy on purpose, and the loss is bounded by the collision check at registration (§4.1.1):
    /// two machines that normalise to one name are refused rather than silently merged, which is
    /// the failure being visible instead of being a shared identity.
    /// </para>
    /// </remarks>
    public static string Sanitise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "localhost";
        }

        var cleaned = new string([.. raw.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-')]);

        // A leading dot or hyphen is outside the grammar's first character class, and trimming
        // is better than prefixing something invented: 'x-server-b' would be a name nobody typed.
        var trimmed = cleaned.TrimStart('.', '-');

        if (trimmed.Length == 0)
        {
            return "localhost";
        }

        return trimmed.Length <= AgentName.MaxMachineLength
            ? trimmed
            : trimmed[..AgentName.MaxMachineLength];
    }

    /// <summary>
    /// Builds this machine's name for a role.
    /// </summary>
    /// <param name="role">The role a launch configuration supplied.</param>
    /// <param name="name">The full name, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if the role is well formed.</returns>
    /// <remarks>
    /// The reason a launch configuration still names only a role: an operator writes
    /// <c>PARLEY_AGENT: "docs"</c> and gets <c>docs@theirmachine</c>, and the same file works on
    /// every machine it is checked out on. Requiring the whole name in configuration would make
    /// the one part that must not be configurable the one part everybody types.
    /// </remarks>
    public static bool TryNameHere(string? role, out AgentName name) =>
        AgentName.TryJoin(role, Current, out name);
}
