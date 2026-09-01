namespace Parley.Core;

/// <summary>
/// One agent waiting on another, for an operator to read.
/// </summary>
/// <param name="Waiter">The agent that asked and has not been answered.</param>
/// <param name="WaitedOn">The agent that owes the answer.</param>
/// <param name="ExchangeId">The exchange they are waiting on.</param>
/// <remarks>
/// Deliberately not <see cref="WaitForGraph"/>. That type exists to answer one question — would
/// this new question close a cycle — and it answers it without ever being read back, which is
/// right for its purpose and wrong for this one. A display operation that returned it would hand
/// a caller something it cannot enumerate, and the caller would end up rebuilding what it was
/// given.
/// </remarks>
public sealed record WaitingEdge(AgentName Waiter, AgentName WaitedOn, long ExchangeId);
