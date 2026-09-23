namespace Parley.Client;

/// <summary>
/// What may name a conversation, as a rule a field can apply while somebody is still typing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule was only ever enforced after the fact, and the owner met it.</b> They named a
/// broadcast <c>tell everybody test</c>, filled in the form, sent it, and got a sentence back
/// about which characters a name may hold — with the form closed and what they had written gone.
/// A rule that can be applied as it is typed should be: a field that will not take a space cannot
/// fail that way. <c>Naming.NameCorrection</c> made exactly this argument for a project's name and
/// this field was the one left out.
/// </para>
/// <para>
/// <b>Here rather than on either surface</b>, for the reason <c>Parley.Client</c> exists at all:
/// the terminal's composer and the console's <i>Start a conversation called</i> field ask the same
/// question, and two implementations of one rule is how they come to disagree about which names
/// are usable. <see cref="Seating.SeatVoice"/>'s own check reads this too, so what a field
/// applies and what the send refuses cannot drift apart.
/// </para>
/// <para>
/// <b>It is not the guard.</b> The invariant lives in the schema, where SPEC.md §9.3 argues it
/// belongs — <c>001_initial.sql</c>'s <c>CHECK</c> on <c>conversation_id</c>, which refuses path
/// syntax and a run of dots because the name reaches a filename when a discussion is exported.
/// Nothing here can be relied on by anything: a correction is a courtesy to the person typing.
/// </para>
/// </remarks>
public static class ConversationName
{
    /// <summary>The most characters a name may hold, per the schema's <c>CHECK</c>.</summary>
    public const int Longest = 64;

    /// <summary>What a field says when <see cref="Correct"/> changed what was typed.</summary>
    /// <remarks>
    /// Required wherever the correction is offered. A field that silently rewrites what somebody
    /// typed is worse than one that refuses it: they are left believing they typed something they
    /// did not, and this one rewrites a token they may be about to say out loud to a colleague.
    /// </remarks>
    public const string WasCorrected =
        "spaces became hyphens and anything but letters, digits, '.', '_' and '-' was dropped";

    /// <summary>The rule in one line, for a field to carry above itself.</summary>
    public const string Rule =
        "Letters, digits, '.', '_' and '-', up to 64 characters. Spaces become hyphens.";

    /// <summary>The same rule as a regular expression, for an HTML <c>pattern</c> attribute.</summary>
    /// <remarks>
    /// Derived from nothing — written once, here, next to the code that applies it, so a console
    /// field and a terminal field cannot come to hold two spellings of one rule. The lookahead is
    /// what carries the <c>..</c> half, which a character class cannot say.
    /// </remarks>
    public const string Pattern = @"(?!.*\.\.)[A-Za-z0-9._-]{1,64}";

    /// <summary>The characters <see cref="Correct"/> keeps, as a character class.</summary>
    /// <remarks>
    /// Read by the console's field, which applies this rule in the browser as the terminal applies
    /// it in a form. A character class rather than a whole expression because what the field does
    /// with it is keep the characters that match and drop the rest.
    /// </remarks>
    public const string Allowed = "[A-Za-z0-9._-]";

    /// <summary>Turns what somebody typed into something that can name a conversation.</summary>
    /// <param name="typed">What is in the field.</param>
    /// <returns>What the field should hold instead.</returns>
    /// <remarks>
    /// <para>
    /// <b>A space becomes a hyphen rather than being dropped</b>, which is the one place this
    /// departs from the project-name correction beside it. Dropping turns
    /// <c>tell everybody test</c> into <c>telleverybodytest</c> — a name that survives being
    /// written down and cannot be read back — where a hyphen is the separator the rule already
    /// allows and keeps the words somebody chose. It is a substitution rather than a guess at
    /// intent: nothing is added, nothing is reordered, and the note says what happened.
    /// </para>
    /// <para>
    /// <b>It corrects and never completes.</b> An empty name, a name past
    /// <see cref="Longest"/> and a run of dots are left for <see cref="Problem"/> to refuse —
    /// truncating somebody's text or collapsing their dots is deciding what they meant, and a
    /// length is a rule they can act on while the words are still in front of them.
    /// </para>
    /// <para>
    /// Stable under a second application, which is what lets a field re-check itself after
    /// setting its own text: everything a correction produces is already acceptable.
    /// </para>
    /// </remarks>
    public static string Correct(string? typed)
    {
        if (string.IsNullOrEmpty(typed))
        {
            return string.Empty;
        }

        var made = new System.Text.StringBuilder(typed.Length);
        var wasSpace = false;

        foreach (var character in typed)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            {
                _ = made.Append(character);
                wasSpace = false;
            }
            else if (char.IsWhiteSpace(character))
            {
                // A run of spaces becomes one hyphen, as `NameCorrection` does it and as the
                // console's own field does it. Two spaces are a slip nobody can see, and `--` in a
                // name is that slip made permanent.
                if (!wasSpace)
                {
                    _ = made.Append('-');
                }

                wasSpace = true;
            }
        }

        return made.ToString();
    }

    /// <summary>Why a name cannot be used, or null when it can.</summary>
    /// <param name="typed">The name.</param>
    /// <returns>The sentence to show, or null.</returns>
    /// <remarks>
    /// The sentence says the rule rather than "invalid", and says why the rule exists, because
    /// the person reading it has to invent a name that satisfies something they cannot see. It
    /// quotes what they typed: with <see cref="Correct"/> applied the quoted text holds nothing
    /// but the characters listed here, and a caller taking a name from somewhere else — a query
    /// string, a message off the wire — is neutralising it for its own surface anyway.
    /// </remarks>
    public static string? Problem(string? typed) =>
        typed is { Length: > 0 and <= Longest }
        && !typed.Contains("..", StringComparison.Ordinal)
        && typed.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            ? null
            : $"'{typed}' cannot name a conversation. Up to {Longest} characters of letters, "
              + "digits, '.', '_' and '-', with no '..' — the name reaches a filename when a "
              + "discussion is exported.";
}
