using System.Diagnostics.CodeAnalysis;

namespace Parley.Core;

/// <summary>
/// What is wrong with a candidate name for a shared file, or that nothing is.
/// </summary>
/// <remarks>
/// Kept apart rather than folded into one refusal, because two of these need different words in front of an
/// agent: a name with the wrong extension is answered with how to place the file anyway, and every other
/// problem only with the rule.
/// </remarks>
public enum SharedFileNameProblem
{
    /// <summary>The name is acceptable.</summary>
    None,

    /// <summary>No name was given.</summary>
    Empty,

    /// <summary>The name does not end in <c>.md</c> or <c>.txt</c>.</summary>
    ExtensionNotAllowed,

    /// <summary>The name contains <c>/</c> or <c>\</c>, and so would be a path.</summary>
    PathSeparator,

    /// <summary>The name contains a character some disk refuses: <c>: * ? " &lt; &gt; |</c>, or broken text.</summary>
    ForbiddenCharacter,

    /// <summary>The name contains a control character.</summary>
    ControlCharacter,

    /// <summary>The name begins with a dot or a space.</summary>
    LeadingDotOrSpace,

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
/// <b>Only <c>.md</c> and <c>.txt</c>.</b> The owner's rule, because a person may open a placed file with
/// the application their system registers for it, and for most other extensions that application runs
/// code. Code travels as text: <c>PriceSnapshot.cs.txt</c>. Only the name is checked; the bytes never are.
/// </para>
/// <para>
/// <b>Names are compared by <see cref="Key"/>, folded for every letter.</b> Windows and macOS hold
/// <c>Übersicht.md</c> and <c>übersicht.md</c> as one file, so two rows for them would let two agents each
/// believe they own it. SQLite's <c>NOCASE</c> folds only ASCII, which is why the key is computed here and
/// stored rather than left to a collation.
/// </para>
/// <para>
/// <b>Case only, not Unicode normalisation, and that was found rather than chosen.</b> The first version
/// normalised to form C before folding, so a name macOS hands back decomposed would match one typed composed.
/// This repository builds with <c>InvariantGlobalization</c>, under which <see cref="string.Normalize()"/>
/// leaves non-ASCII text as it is — the test comparing the two spellings failed, and the key it produced was
/// a promise the build could not keep. Case folding for every letter does work in that mode. So the two
/// spellings of one accented name are two names here; SPEC.md §9.5 says so.
/// </para>
/// <para>
/// <b>Lengths count characters as SQLite does</b> — Unicode scalar values, not UTF-16 units — because the
/// schema's own bound is <c>length(name)</c> and a name this type accepted must not be one the store refuses.
/// </para>
/// </remarks>
public sealed class SharedFileName : IEquatable<SharedFileName>
{
    /// <summary>The longest a file's path inside the location may be: its folder, a separator and its name.</summary>
    /// <remarks>
    /// The owner's figure, counted without the location's own path, so the same names are legal wherever a
    /// broker keeps its store.
    /// </remarks>
    public const int MaxPathCharacters = 255;

    /// <summary>The longest a name may be before any folder is considered.</summary>
    /// <remarks>The path bound less the separator; a real folder leaves less, which <see cref="FitsIn"/> checks.</remarks>
    public const int MaxNameCharacters = MaxPathCharacters - 1;

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

    /// <summary>The name folded for every letter, which is what names are compared and keyed by.</summary>
    public string Key { get; }

    /// <summary>Says what is wrong with a candidate name, if anything.</summary>
    /// <param name="candidate">The name.</param>
    /// <returns>The first problem found, or <see cref="SharedFileNameProblem.None"/>.</returns>
    public static SharedFileNameProblem Check(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return SharedFileNameProblem.Empty;
        }

        foreach (var ch in candidate)
        {
            if (ch < 0x20 || ch == 0x7F)
            {
                return SharedFileNameProblem.ControlCharacter;
            }
        }

        if (candidate.Contains('/', StringComparison.Ordinal) || candidate.Contains('\\', StringComparison.Ordinal))
        {
            return SharedFileNameProblem.PathSeparator;
        }

        if (candidate.AsSpan().IndexOfAny(":*?\"<>|") >= 0 || !IsWellFormed(candidate))
        {
            return SharedFileNameProblem.ForbiddenCharacter;
        }

        if (candidate[0] is '.' or ' ')
        {
            return SharedFileNameProblem.LeadingDotOrSpace;
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

        return CharacterCount(candidate) > MaxNameCharacters
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
        SharedFileNameProblem.PathSeparator => "A file name is one name, not a path, so it cannot contain / or \\.",
        SharedFileNameProblem.ForbiddenCharacter =>
            "A file name cannot contain : * ? \" < > |, which some disks refuse.",
        SharedFileNameProblem.ControlCharacter => "A file name cannot contain control characters.",
        SharedFileNameProblem.LeadingDotOrSpace => "A file name cannot begin with a dot or a space.",
        SharedFileNameProblem.ReservedDeviceName =>
            "Windows reserves that name (CON, PRN, AUX, NUL, COM1 to COM9, LPT1 to LPT9), with or without an " +
            "extension, so no file can have it.",
        SharedFileNameProblem.TooLong => $"A file name is at most {MaxNameCharacters} characters.",
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, null),
    };

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

    private static bool IsWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
