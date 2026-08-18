namespace Parley.Core;

/// <summary>
/// The result of attempting a state transition on an <see cref="Exchange"/>.
/// </summary>
/// <param name="Succeeded">Whether the transition was applied.</param>
/// <param name="Error">
/// The reason for refusal, or <see cref="TransitionError.None"/> on success.
/// </param>
public readonly record struct TransitionResult(bool Succeeded, TransitionError Error)
{
    /// <summary>
    /// Gets the result representing a successfully applied transition.
    /// </summary>
    public static TransitionResult Ok { get; } = new(true, TransitionError.None);

    /// <summary>
    /// Creates a result representing a refused transition.
    /// </summary>
    /// <param name="error">The reason for refusal.</param>
    /// <returns>A failed result carrying <paramref name="error"/>.</returns>
    public static TransitionResult Fail(TransitionError error) => new(false, error);
}
