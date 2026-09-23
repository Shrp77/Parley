namespace Parley.Core;

/// <summary>
/// One participant waiting on another, for the person running the deployment to read.
/// </summary>
/// <param name="Waiter">The participant waiting.</param>
/// <param name="WaitedOn">The participant that owes the next move.</param>
/// <param name="ExchangeId">The exchange they are waiting on.</param>
/// <remarks>
/// Deliberately not <see cref="WaitForGraph"/>. That type answers whether a new Request closes a cycle
/// and is never read back, which is right for its purpose and useless for showing somebody what is
/// stuck.
/// </remarks>
public sealed record WaitingEdge(AgentName Waiter, AgentName WaitedOn, long ExchangeId);
