namespace Parley.Core;

/// <summary>The ceilings on what an agent may read of the project's past (SPEC.md §10.1, §13).</summary>
/// <remarks>
/// One place, so the broker, the adapter's description and the tests cannot drift. Text an agent reads is charged to its
/// context like any text that crosses (principle 2.2), so every one of these is a design bound and not a tuning knob; the
/// owner's to change, and recorded in §13 as a reduction in that control.
/// </remarks>
public static class HistoryLimits
{
    /// <summary>The most characters one reply of an exchange carries, though never fewer than one message.</summary>
    public const int ReplyCharacters = 64_000;

    /// <summary>The most matches one page of a search carries.</summary>
    public const int PageMatches = 20;

    /// <summary>The most characters of a snippet.</summary>
    public const int SnippetCharacters = 240;

    /// <summary>The most characters of the words searched for.</summary>
    public const int WordsCharacters = 200;

    /// <summary>What a search is charged against <see cref="CharactersPerMinute"/> for each millisecond it takes.</summary>
    /// <remarks>A search scans the store while it holds the broker's gate, and what it costs depends on what the store holds, so it is charged by the time it took.</remarks>
    public const int CharactersPerMillisecond = 100;

    /// <summary>The longest a search may run before it is stopped and refused, in milliseconds.</summary>
    /// <remarks>A search holds the broker's gate while it scans, and how long that takes depends on what the store holds, so no search is let run without end.</remarks>
    public const int SearchTimeLimitMilliseconds = 1_500;

    /// <summary>The least a search is charged against <see cref="CharactersPerMinute"/>, however quick it was.</summary>
    public const int SearchCharge = 20_000;

    /// <summary>The most readers of one exchange the person is told of by name; the rest are counted.</summary>
    public const int ReadersNamed = 5;

    /// <summary>The most agents whose searches the person is shown beside a conversation.</summary>
    public const int SearchersShown = 8;

    /// <summary>The most calls one agent makes in a minute.</summary>
    public const int CallsPerMinute = 30;

    /// <summary>The most characters one agent is returned in a minute.</summary>
    public const int CharactersPerMinute = 400_000;

    /// <summary>What a search is charged against <see cref="CharactersPerMinute"/>.</summary>
    /// <param name="elapsedMilliseconds">How long it took.</param>
    /// <param name="returnedCharacters">How much it returned.</param>
    /// <returns>The time it took at <see cref="CharactersPerMillisecond"/>, never less than <see cref="SearchCharge"/>, and what it returned.</returns>
    /// <remarks>Clamped, so a clock that runs backwards charges the floor and a very long search cannot overflow.</remarks>
    public static int SearchCost(long elapsedMilliseconds, int returnedCharacters)
    {
        const int Ceiling = int.MaxValue / 4;

        var byTime = elapsedMilliseconds <= 0 ? 0
            : elapsedMilliseconds >= Ceiling / CharactersPerMillisecond ? Ceiling
            : (int)(elapsedMilliseconds * CharactersPerMillisecond);

        return Math.Max(SearchCharge, byTime) + Math.Max(0, returnedCharacters);
    }
}
