namespace Parley.Core;

/// <summary>
/// Turning what somebody typed into the shape a name here has to be.
/// </summary>
/// <remarks>
/// <para>
/// <b>One grammar, three fields.</b> <see cref="ProjectName"/> and the role half of
/// <see cref="AgentName"/> are the same expression — a letter, then letters, digits and hyphens,
/// up to thirty-two characters — and a person types into three fields held to it: a project's
/// name, an agent's role when a repository is enrolled, and the name somebody joins under. The
/// correction belongs beside the grammar rather than beside each field, or the surfaces would
/// come to disagree about what a name may hold.
/// </para>
/// <para>
/// <b>Why a correction exists at all.</b> A rule that can be applied as it is typed should be:
/// a capitalised name such as <c>OrderLedger</c>, or a conversation named <c>tell everybody test</c>,
/// would otherwise be refused only after the whole form was filled in. A field that will not take a
/// capital or a space cannot fail either of those ways.
/// </para>
/// <para>
/// <b>It corrects and never completes.</b> An empty name, and one whose first character is a digit
/// or a hyphen, are left for the rule itself to refuse: guessing what somebody meant is how a
/// field comes to hold a name they did not choose, and these name a database, an agent every peer
/// addresses, and a person.
/// </para>
/// </remarks>
public static class NameCorrection
{
    /// <summary>What a field says when <see cref="Correct"/> changed what was typed.</summary>
    /// <remarks>
    /// Required wherever the correction is offered. Nothing is rewritten silently: a field that
    /// quietly replaces what somebody typed leaves them believing they typed something else, which
    /// is worse than refusing it outright.
    /// </remarks>
    public const string WasCorrected =
        "capitals were lowered, spaces became hyphens and anything else was dropped";

    /// <summary>The characters a correction keeps once case has been folded.</summary>
    /// <remarks>
    /// A character class, for a browser field applying the same rule as it is typed. Read by the
    /// console; the terminal calls <see cref="Correct"/> itself.
    /// </remarks>
    public const string Allowed = "[a-z0-9-]";

    /// <summary>Makes what was typed into something the grammar can accept.</summary>
    /// <param name="typed">What is in the field.</param>
    /// <returns>What the field should hold instead.</returns>
    /// <remarks>
    /// <para>
    /// <b>A space becomes a hyphen rather than being dropped.</b> Dropping turns
    /// <c>order ledger</c> into <c>orderledger</c>, which nobody can read back; a hyphen is the
    /// separator the grammar already allows, so the words somebody chose survive. It is a
    /// substitution rather than a guess: nothing is added and nothing is reordered.
    /// </para>
    /// <para>
    /// A run of spaces becomes one hyphen rather than one each, which is the only place this does
    /// anything other than map character for character. Two spaces are a typing artefact, and the
    /// alternative is a name carrying <c>--</c> from a slip somebody cannot see.
    /// </para>
    /// <para>
    /// Stable under a second application, which is what lets a field re-check itself after setting
    /// its own text: everything this produces is already acceptable to it.
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
            var lowered = char.ToLowerInvariant(character);

            if (char.IsAsciiLetterLower(lowered) || char.IsAsciiDigit(lowered) || lowered == '-')
            {
                _ = made.Append(lowered);
                wasSpace = false;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (!wasSpace)
                {
                    _ = made.Append('-');
                }

                wasSpace = true;
            }
        }

        return made.ToString();
    }
}
