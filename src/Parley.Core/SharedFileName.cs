using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Parley.Core;

/// <summary>
/// What is wrong with a candidate name for a shared file, or that nothing is.
/// </summary>
/// <remarks>
/// Kept apart rather than folded into one refusal, because two of these need different words in front of an
/// agent: a name with the wrong extension is answered with how to place the file anyway, and a name with a
/// character outside the set is answered with the spelling it would be placed under.
/// </remarks>
public enum SharedFileNameProblem
{
    /// <summary>The name is acceptable.</summary>
    None,

    /// <summary>No name was given.</summary>
    Empty,

    /// <summary>The name does not end in <c>.md</c> or <c>.txt</c>.</summary>
    ExtensionNotAllowed,

    /// <summary>
    /// The name holds a character other than <c>A</c>–<c>Z</c>, <c>a</c>–<c>z</c>, <c>0</c>–<c>9</c>, <c>.</c>,
    /// <c>_</c> and <c>-</c>.
    /// </summary>
    DisallowedCharacter,

    /// <summary>The name begins with a dot.</summary>
    LeadingDot,

    /// <summary>The name is a device name Windows reserves, with or without an extension.</summary>
    ReservedDeviceName,

    /// <summary>The name is longer than any folder could hold.</summary>
    TooLong,
}

/// <summary>
/// A name a shared file may have in the file location (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation of the rules, read by the adapter, the broker and every surface</b>, so what a
/// surface offers and what the store refuses cannot disagree. The schema holds the same rules again
/// (§9.3); this is the one that can say what was wrong.
/// </para>
/// <para>
/// <b>Only <c>.md</c> and <c>.txt</c>.</b> A person may open a placed file with
/// the application their system registers for it, and for most other extensions that application runs
/// code. Code travels as text: <c>PriceSnapshot.cs.txt</c>. Only the name is checked; the bytes never are.
/// </para>
/// <para>
/// <b>Only letters A to Z, digits, <c>.</c>, <c>_</c> and <c>-</c>, and a placement is renamed rather than
/// refused.</b> A list of what some disk refuses would let a name reaching another agent's model carry up to
/// two hundred and fifty characters of the placer's choosing, spaces and all, and would need case folded for
/// every letter, which the build's <c>InvariantGlobalization</c> can only half do. With the set, every name is
/// ASCII, the key is the store's own <c>upper()</c>, and two spellings of one accented name cannot exist.
/// </para>
/// <para>
/// <see cref="Safe"/> replaces each character outside the set with <c>_</c>, one for one, so a renamed
/// placement is the same length as the name asked for. The broker renames; nothing else does. A name
/// arriving anywhere else — a fetch, a removal, a file a person copied in by hand — is checked as it is,
/// because renaming a name somebody is looking <i>up</i> would find a different file from the one they
/// meant.
/// </para>
/// </remarks>
public sealed class SharedFileName : IEquatable<SharedFileName>
{
    /// <summary>The longest a file's path inside the location may be: its folder, a separator and its name.</summary>
    /// <remarks>
    /// Counted without the location's own path, so the same names are legal wherever a broker keeps its store.
    /// </remarks>
    public const int MaxPathCharacters = 255;

    /// <summary>The longest a name may be before any folder is considered.</summary>
    /// <remarks>The path bound less the separator; a real folder leaves less, which <see cref="FitsIn"/> checks.</remarks>
    public const int MaxNameCharacters = MaxPathCharacters - 1;

    /// <summary>What a character outside the set becomes when a placement is renamed.</summary>
    public const char Replacement = '_';

    /// <summary>The set, in the words every refusal and tool description uses.</summary>
    public const string AllowedCharactersInWords = "letters A to Z and a to z, digits, '.', '_' and '-'";

    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private SharedFileName(string value)
    {
        Value = value;
        Key = KeyOf(value);
    }

    /// <summary>The name as it was placed.</summary>
    public string Value { get; }

    /// <summary>The name in upper case, which is what names are compared and keyed by.</summary>
    public string Key { get; }

    /// <summary>Whether a character is in the set a name is made of.</summary>
    /// <param name="ch">The character.</param>
    /// <returns><see langword="true"/> for an ASCII letter or digit, <c>.</c>, <c>_</c> or <c>-</c>.</returns>
    public static bool IsAllowed(char ch) => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-';

    /// <summary>The spelling a placement is stored under: every character outside the set replaced with <c>_</c>.</summary>
    /// <param name="candidate">The name asked for.</param>
    /// <returns>
    /// The safe spelling, one character for each character of the candidate — a surrogate pair is one — or the
    /// candidate itself when nothing needed replacing.
    /// </returns>
    /// <remarks>
    /// One for one rather than collapsing runs, so a person comparing the two names reads them column by column,
    /// and so the length rules measure the same thing before and after.
    /// </remarks>
    public static string Safe(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.All(IsAllowed))
        {
            return candidate;
        }

        var safe = new StringBuilder(candidate.Length);

        for (var i = 0; i < candidate.Length; i++)
        {
            var ch = candidate[i];

            if (IsAllowed(ch))
            {
                safe.Append(ch);
                continue;
            }

            if (char.IsHighSurrogate(ch) && i + 1 < candidate.Length && char.IsLowSurrogate(candidate[i + 1]))
            {
                i++;
            }

            safe.Append(Replacement);
        }

        return safe.ToString();
    }

    /// <summary>Says what is wrong with a candidate name, if anything.</summary>
    /// <param name="candidate">The name.</param>
    /// <returns>The first problem found, or <see cref="SharedFileNameProblem.None"/>.</returns>
    public static SharedFileNameProblem Check(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return SharedFileNameProblem.Empty;
        }

        if (!candidate.All(IsAllowed))
        {
            return SharedFileNameProblem.DisallowedCharacter;
        }

        if (candidate[0] == '.')
        {
            return SharedFileNameProblem.LeadingDot;
        }

        if (!candidate.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !candidate.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return SharedFileNameProblem.ExtensionNotAllowed;
        }

        var stem = candidate[..candidate.IndexOf('.', StringComparison.Ordinal)];

        if (Reserved.Contains(stem.ToUpperInvariant(), StringComparer.Ordinal))
        {
            return SharedFileNameProblem.ReservedDeviceName;
        }

        return candidate.Length > MaxNameCharacters
            ? SharedFileNameProblem.TooLong
            : SharedFileNameProblem.None;
    }

    /// <summary>Tries to make a name.</summary>
    /// <param name="candidate">The name.</param>
    /// <param name="name">The name, when it is acceptable.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool TryCreate(string? candidate, [NotNullWhen(true)] out SharedFileName? name)
    {
        name = Check(candidate) == SharedFileNameProblem.None ? new SharedFileName(candidate!) : null;
        return name is not null;
    }

    /// <summary>Makes a name, or says why it cannot be one.</summary>
    /// <param name="candidate">The name.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentException">The name breaks a rule; the message says which.</exception>
    public static SharedFileName Create(string? candidate)
    {
        var problem = Check(candidate);

        return problem == SharedFileNameProblem.None
            ? new SharedFileName(candidate!)
            : throw new ArgumentException(Describe(problem), nameof(candidate));
    }

    /// <summary>The rule a problem breaks, in words an agent or a person can act on.</summary>
    /// <param name="problem">The problem.</param>
    /// <returns>The sentence.</returns>
    public static string Describe(SharedFileNameProblem problem) => problem switch
    {
        SharedFileNameProblem.None => "The name is acceptable.",
        SharedFileNameProblem.Empty => "A file needs a name.",
        SharedFileNameProblem.ExtensionNotAllowed =>
            "Only .md and .txt files can be placed. To place anything else, add .txt to its name, such as " +
            "PriceSnapshot.cs.txt.",
        SharedFileNameProblem.DisallowedCharacter => $"A file name uses only {AllowedCharactersInWords}.",
        SharedFileNameProblem.LeadingDot => "A file name cannot begin with a dot.",
        SharedFileNameProblem.ReservedDeviceName =>
            "Windows reserves that name (CON, PRN, AUX, NUL, COM1 to COM9, LPT1 to LPT9), with or without an " +
            "extension, so no file can have it.",
        SharedFileNameProblem.TooLong => $"A file name is at most {MaxNameCharacters} characters.",
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, null),
    };

    /// <summary>
    /// Why a name that is being looked up is not one, and — when the only fault is its characters — the spelling a
    /// placement of it would have been stored under.
    /// </summary>
    /// <param name="candidate">The name as the caller wrote it.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    /// The spelling is offered rather than used. A peer told about <c>price notes.md</c> most likely means
    /// <c>price_notes.md</c>, but <c>price:notes.md</c> means that too, and one of them may have been refused for
    /// colliding with the other — so looking up the renamed spelling could hand over a different file.
    /// </remarks>
    public static string DescribeLookup(string? candidate)
    {
        var problem = Check(candidate);

        if (problem != SharedFileNameProblem.DisallowedCharacter)
        {
            return Describe(problem);
        }

        var safe = Safe(candidate!);

        return Check(safe) == SharedFileNameProblem.None
            ? $"{Describe(problem)} A file placed as that name is stored as '{safe}'; ask for it by that name."
            : Describe(problem);
    }

    /// <summary>The refusal of a renamed placement whose safe spelling another file already has.</summary>
    /// <param name="asked">The name the placement asked for.</param>
    /// <param name="safe">The spelling it would have been stored under.</param>
    /// <param name="existing">The name of the file already there, as it is stored.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    /// Refused rather than replaced even when the caller placed the file already there: <c>a:b.md</c> and
    /// <c>a?b.md</c> are both <c>a_b.md</c>, and a placement that silently replaced one with the other would be a
    /// rename nobody asked for. Placing again under the safe spelling replaces it on purpose. The name asked for is
    /// quoted with its control characters made visible, since it is the one thing in the sentence nothing checked.
    /// </remarks>
    public static string SafeNameTaken(string asked, string safe, string existing)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(safe);
        ArgumentNullException.ThrowIfNull(existing);

        var shown = new string(asked.Select(c => char.IsControl(c) ? '?' : c).ToArray());

        return $"File '{shown}' has illegal characters and will be named '{safe}', which collides with an existing " +
               $"file with that name ('{existing}'). Nothing was placed. Choose another name, or place it as " +
               $"'{safe}' to replace that file if it is yours.";
    }

    /// <summary>The key a name is compared by: every letter in upper case.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.ToUpperInvariant();
    }

    /// <summary>How many characters text is, counted as SQLite's <c>length()</c> counts them.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The number of Unicode scalar values.</returns>
    /// <remarks>
    /// Counted as scalar values although a name is ASCII: a folder is measured with it too, and what a
    /// placement asked for is measured before it is made safe.
    /// </remarks>
    public static int CharacterCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.EnumerateRunes().Count();
    }

    /// <summary>How many characters a folder leaves for a name.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <returns>The longest name that fits, which may be less than one.</returns>
    public static int CharactersLeftIn(string folder) =>
        Math.Min(MaxNameCharacters, MaxPathCharacters - 1 - CharacterCount(folder));

    /// <summary>Whether this name fits inside a folder under the path bound.</summary>
    /// <param name="folder">The conversation's folder name.</param>
    /// <returns><see langword="true"/> when the folder, a separator and the name are at most 255 characters.</returns>
    public bool FitsIn(string folder) => CharacterCount(Value) <= CharactersLeftIn(folder);

    /// <inheritdoc/>
    public bool Equals(SharedFileName? other) =>
        other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SharedFileName);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);

    /// <inheritdoc/>
    public override string ToString() => Value;
}
