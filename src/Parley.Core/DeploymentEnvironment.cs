namespace Parley.Core;

/// <summary>
/// The environment variables that name a deployment's shared facts.
/// </summary>
/// <remarks>
/// <para>
/// Here for the reason <see cref="RetentionPolicyEnvironment"/> is here, and it is the same
/// reason: two binaries read these — the agents' servers and the operator's tool — and neither
/// references the other, so a name spelled in both places is a name that can drift in one.
/// </para>
/// <para>
/// <see cref="DatabaseVariable"/> was exactly that. It was a constant in the MCP server and a
/// bare string literal in the operator's command, agreeing by nothing but coincidence. Renaming
/// one would have left the operator administering a database the agents were not using while
/// both reported success — the same silent wrong-file failure that pointing the tool at somebody
/// else's SQLite file once produced, reached from the other direction and with nothing to notice
/// it. That the strings happened to match is not a check.
/// </para>
/// <para>
/// Nothing here is I/O. These are names; reading them is the host's business, and doing it
/// through a delegate is what keeps this assembly free of both.
/// </para>
/// </remarks>
public static class DeploymentEnvironment
{
    /// <summary>The variable naming the project's database file.</summary>
    /// <remarks>
    /// One file per project, per SPEC.md §4.1: independent projects do not share a broker, and
    /// the isolation is the file boundary rather than a column anyone could forget to filter on.
    /// So this variable is what says which project a process belongs to, and the operator's tool
    /// and the agents' servers must read the same one or they are looking at different
    /// deployments.
    /// </remarks>
    public const string DatabaseVariable = "PARLEY_DB";
}
