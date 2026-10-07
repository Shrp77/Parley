namespace Parley.Core;

/// <summary>
/// How long a turn may run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here because it is the only place both readers can reach.</b> The node's invoker lives in
/// <c>Parley.Headless</c> and the broker's service in <c>Parley.Server</c>; neither references the
/// other. <see cref="MessageLimits"/> is already here on the same footing: a protocol bound that
/// things far apart must agree on, stated once.
/// </para>
/// </remarks>
public static class TurnBudget
{
    /// <summary>The longest one turn may run before the machine hosting it gives up.</summary>
    /// <remarks>
    /// Twenty minutes is what the node has always allowed, kept rather than chosen: this change is
    /// about two numbers agreeing, and moving the one that was never wrong would have made it a
    /// different change. A turn is a model working on a real repository, and the cases that reach
    /// this are the ones worth waiting for.
    /// </remarks>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(20);
}
