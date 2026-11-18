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

    /// <summary>What a search is charged against <see cref="CharactersPerMinute"/>, whatever it returns.</summary>
    /// <remarks>A search scans the whole store, so it costs the same whether it finds nothing or a page; without a charge only the call count would limit it.</remarks>
    public const int SearchCharge = 20_000;

    /// <summary>The most rows of its own reads and searches the store keeps for one agent, for the person to see.</summary>
    public const int RecordsPerReader = 2_000;

    /// <summary>The most calls one agent makes in a minute.</summary>
    public const int CallsPerMinute = 30;

    /// <summary>The most characters one agent is returned in a minute.</summary>
    public const int CharactersPerMinute = 400_000;
}
