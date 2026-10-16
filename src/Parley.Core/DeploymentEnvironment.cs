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
