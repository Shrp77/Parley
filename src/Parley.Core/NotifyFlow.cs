namespace Parley.Core;

/// <summary>
/// What one agent has sent another as notifications.
/// </summary>
/// <param name="From">The sender.</param>
/// <param name="To">The recipient.</param>
/// <param name="Sent">How many notifications.</param>
/// <param name="Characters">Their total size, measured as rule 8.5 measures a message.</param>
/// <param name="Largest">The biggest single one.</param>
/// <param name="FirstUtc">When the first was sent.</param>
/// <param name="LastUtc">When the most recent was sent.</param>
public sealed record NotifyFlow(
    string From, string To, long Sent, long Characters, long Largest,
    string FirstUtc, string LastUtc)
{
    /// <summary>Gets the mean size of a notification on this flow.</summary>
    public long Mean => Sent == 0 ? 0 : Characters / Sent;
}
