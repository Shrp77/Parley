using Parley.Core;

namespace Parley.Store;

/// <summary>The agent a machine holds under an identity, and where it said it lives.</summary>
/// <param name="Name">The name it is registered under now.</param>
/// <param name="Workspace">The workspace recorded for it, or null when none was recorded.</param>
public sealed record IdentityHolder(AgentName Name, string? Workspace);
