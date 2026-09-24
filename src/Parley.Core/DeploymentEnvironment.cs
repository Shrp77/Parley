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
    /// <summary>The variable that used to name a project's database file. <b>Nothing reads it.</b></summary>
    /// <remarks>
    /// <para>
    /// <b>Kept as a name, not as a mechanism.</b> 019 removed it along with <c>--db</c>: a project's
    /// store is <c>parley.db</c> inside its own folder, derived from its name, so there is nothing for a
    /// variable to say. What this constant is for now is the tests that assert it is <i>ignored</i> —
    /// a check spelled with a literal would keep passing if somebody reintroduced the variable under a
    /// slightly different name.
    /// </para>
    /// <para>
    /// The paragraph that stood here argued the opposite, and is worth keeping because the reasoning is
    /// still right about the thing it examined: one file per project, per SPEC.md §4.1, the isolation
    /// being the file boundary rather than a column anyone could forget to filter on. What was wrong was
    /// concluding that a <i>variable</i> should say which project a process belongs to. Two readers of
    /// one variable is two places the answer lives, and the review gate found both ways that can go
    /// wrong before it was removed.
    /// </para>
    /// </remarks>
    public const string DatabaseVariable = "PARLEY_DB";

    /// <summary>The variable saying what an agent owns.</summary>
    /// <remarks>
    /// Read by a server, which records it, and named by the operator's tool, which reports the
    /// agents that have not set it — so it is the second name two binaries share without
    /// referencing each other, and it is here for the reason the first one is. See
    /// <see cref="OwnershipStatement"/> for what the value is and why it comes from a launch
    /// rather than from a call.
    /// </remarks>
    public const string OwnsVariable = "PARLEY_OWNS";
}
