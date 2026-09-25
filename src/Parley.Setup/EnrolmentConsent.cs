using Parley.Core;

namespace Parley.Setup;

/// <summary>
/// What a person is told before any repository is enrolled.
/// </summary>
/// <remarks>
/// <para>
/// <b>FR-044: said before anything is configured, and it names the count.</b> "Some repositories"
/// is a sentence somebody agrees to without weighing; a number is one they can. And what is being
/// agreed to is not filing: enrolling catalogues each repository, so a peer's request can start it
/// with nobody watching, which spends whatever that agent costs to run.
/// </para>
/// <para>
/// <b>One sentence, two surfaces, because the command line did not have it.</b> The terminal's
/// door said all of this and `parley enroll` said only how many repositories it had found — so the
/// same act carried its consequence on one surface and not the other, and the surface without it
/// is the one a script reaches. Two spellings of a consent notice would also drift, and the half
/// that drifts is the half nobody is reading at the time.
/// </para>
/// <para>
/// Public because <c>Parley.Terminal</c> reads it, which is the same reason this whole project is
/// public to the two tools and no further.
/// </para>
/// </remarks>
public static class EnrolmentConsent
{
    /// <summary>What enrolling these repositories will do, in one paragraph.</summary>
    /// <param name="count">How many are about to be enrolled.</param>
    /// <param name="project">The project they become agents of, or null when none is named.</param>
    /// <returns>The sentence to put in front of somebody before anything is written.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static string Says(int count, ProjectName? project)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var of = project is { } named ? $" of {named.Value}" : string.Empty;

        // **Plain interpolation, not `string.Create`.** A concatenated interpolated string is not
        // an interpolated-string handler, so passing one to `string.Create` is CS1620 — "argument 2
        // must be passed with the 'ref' keyword", which names neither the cause nor the fix. This
        // sentence is the terminal's, moved here unchanged, and it built there this way.
        return $"{count} repositories will be configured as agents{of}. This writes into each one, "
            + "and catalogues it so that a peer's request can start it with nobody watching — which "
            + "spends whatever that agent costs to run. Each is then started once so that it "
            + $"registers itself, so expect {count} processes.";
    }
}
