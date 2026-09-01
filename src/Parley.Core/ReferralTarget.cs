namespace Parley.Core;

/// <summary>
/// Where an answerer sent an asker instead.
/// </summary>
/// <param name="Asker">The agent that asked.</param>
/// <param name="Answerer">The agent that referred it onward.</param>
/// <param name="RedirectedTo">The agent named as the owner.</param>
/// <param name="Referrals">How many sub-questions went that way.</param>
/// <param name="LastUtc">When the most recent one was sent.</param>
public sealed record ReferralTarget(
    string Asker, string Answerer, string RedirectedTo, long Referrals, string LastUtc);
