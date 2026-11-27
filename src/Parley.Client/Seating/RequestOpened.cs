namespace Parley.Client.Seating;

/// <summary>What became of one Request a person sent.</summary>
/// <param name="Says">A sentence saying what happened, as a surface shows it.</param>
/// <param name="Exchange">The exchange it opened, or <see langword="null"/> when it was not sent.</param>
/// <param name="Reached">How the receiver was reached, in a few words for a list; <see langword="null"/> when not sent.</param>
public sealed record RequestOpened(string Says, long? Exchange, string? Reached)
{
    /// <summary>Whether it was sent.</summary>
    public bool Sent => Exchange is not null;
}
