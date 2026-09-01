namespace Parley.Core;

/// <summary>
/// How often one agent asked another and was told it was somebody else's.
/// </summary>
/// <param name="Asker">The agent that asked.</param>
/// <param name="Answerer">The agent it asked.</param>
/// <param name="Closed">Exchanges between them that reached a resolution.</param>
/// <param name="OutOfScope">How many of those resolved as not the answerer's.</param>
public sealed record ReferralPair(string Asker, string Answerer, long Closed, long OutOfScope)
{
    /// <summary>Gets the share of closed exchanges the answerer declined as not its own.</summary>
    public double Rate => Closed == 0 ? 0 : (double)OutOfScope / Closed;
}
