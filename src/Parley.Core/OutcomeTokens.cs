namespace Parley.Core;

/// <summary>
/// The words outcomes and blockers are stored and carried as, and how an exchange rolls them up.
/// </summary>
/// <remarks>
/// <para>
/// One place for every spelling, because the same words are checked by the schema's triggers, read
/// back into telemetry, written by adapters and read by models. Two lists of accepted words drift,
/// and the drift shows up as a typo that telemetry counts as something it is not.
/// </para>
/// <para>
/// Parsing throws on a word it does not know rather than guessing. A guess is how a misspelled
/// outcome would become <c>blocked</c> and be counted as a stuck agent.
/// </para>
/// </remarks>
public static class OutcomeTokens
{
    /// <summary>Formats an item outcome.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The stored word.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public static string Format(Outcome outcome) => outcome switch
    {
        Outcome.Done => "done",
        Outcome.Answered => "answered",
        Outcome.Acknowledged => "acknowledged",
        Outcome.Blocked => "blocked",
        Outcome.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    /// <summary>Reads an item outcome.</summary>
    /// <param name="value">The word.</param>
    /// <param name="outcome">The outcome, when the word names one.</param>
    /// <returns><see langword="true"/> if the word is an outcome.</returns>
    public static bool TryParse(string? value, out Outcome outcome)
    {
        switch (value)
        {
            case "done": outcome = Outcome.Done; return true;
            case "answered": outcome = Outcome.Answered; return true;
            case "acknowledged": outcome = Outcome.Acknowledged; return true;
            case "blocked": outcome = Outcome.Blocked; return true;
            case "closed": outcome = Outcome.Closed; return true;
            default: outcome = default; return false;
        }
    }

    /// <summary>Formats a blocker kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The stored word.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public static string Format(BlockerKind kind) => kind switch
    {
        BlockerKind.MissingInformation => "missing_information",
        BlockerKind.Conflict => "conflict",
        BlockerKind.Permission => "permission",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>Reads a blocker kind.</summary>
    /// <param name="value">The word.</param>
    /// <param name="kind">The kind, when the word names one.</param>
    /// <returns><see langword="true"/> if the word is a blocker kind.</returns>
    public static bool TryParse(string? value, out BlockerKind kind)
    {
        switch (value)
        {
            case "missing_information": kind = BlockerKind.MissingInformation; return true;
            case "conflict": kind = BlockerKind.Conflict; return true;
            case "permission": kind = BlockerKind.Permission; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>Formats an exchange's rolled-up outcome.</summary>
    /// <param name="outcome">The outcome, or <see langword="null"/> while open.</param>
    /// <returns>The stored word, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public static string? Format(ExchangeOutcome? outcome) => outcome switch
    {
        null => null,
        ExchangeOutcome.Complete => "complete",
        ExchangeOutcome.Blocked => "blocked",
        ExchangeOutcome.Closed => "closed",
        ExchangeOutcome.Settled => "settled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    /// <summary>Reads an exchange's rolled-up outcome.</summary>
    /// <param name="value">The stored word, or <see langword="null"/>.</param>
    /// <returns>The outcome, or <see langword="null"/> for an open exchange.</returns>
    /// <exception cref="InvalidOperationException">The word is not one the protocol defines.</exception>
    public static ExchangeOutcome? ParseExchange(string? value) => value switch
    {
        null => null,
        "complete" => ExchangeOutcome.Complete,
        "blocked" => ExchangeOutcome.Blocked,
        "closed" => ExchangeOutcome.Closed,
        "settled" => ExchangeOutcome.Settled,
        _ => throw new InvalidOperationException($"Unrecognised exchange outcome '{value}'.")
    };

    /// <summary>Rolls the outcomes of a Result's items up into the exchange's.</summary>
    /// <param name="outcomes">One outcome per item.</param>
    /// <returns><c>closed</c> if any item was, otherwise <c>blocked</c> if any was, otherwise <c>complete</c>.</returns>
    /// <exception cref="ArgumentException">There are no outcomes.</exception>
    /// <remarks>
    /// Derived by the broker rather than supplied by whoever sent the Result, so the two cannot
    /// disagree: a Result blocking every item under an exchange recorded <c>complete</c> would make
    /// §15 describe a deployment that does not exist.
    /// </remarks>
    public static ExchangeOutcome RollUp(IReadOnlyCollection<Outcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        if (outcomes.Count == 0)
        {
            throw new ArgumentException("A Result has an outcome for every item.", nameof(outcomes));
        }

        return outcomes.Contains(Outcome.Closed) ? ExchangeOutcome.Closed
            : outcomes.Contains(Outcome.Blocked) ? ExchangeOutcome.Blocked
            : ExchangeOutcome.Complete;
    }
}
