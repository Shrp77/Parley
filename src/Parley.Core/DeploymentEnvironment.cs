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
/// Nothing here is I/O. These are names; reading them is the host's business, and doing it
/// through a delegate is what keeps this assembly free of both.
/// </para>
/// </remarks>
public static class DeploymentEnvironment
{
    /// <summary>A variable that names no database. <b>Nothing reads it.</b></summary>
    /// <remarks>
    /// A project's store is <c>parley.db</c> inside its own folder, derived from its name (SPEC.md
    /// §4.1), so there is nothing for a variable to say. The constant exists for the tests that assert
    /// it is <i>ignored</i>: a check spelled with a literal would keep passing if somebody
    /// reintroduced the variable under a slightly different name.
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
