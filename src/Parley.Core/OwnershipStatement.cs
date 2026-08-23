using System.Globalization;
using System.Text;

namespace Parley.Core;

/// <summary>
/// One line saying what an agent owns, as an asker reads it when choosing who to address.
/// </summary>
/// <remarks>
/// <para>
/// The registry records a name and no more, so the only routing signal inside the protocol has
/// been the name itself: an asker knows that <c>billing</c> exists and is reachable, and
/// nothing about whether a question turning on tax is billing's. Everything else lived in each
/// repository's standing instructions — one copy per repository, drifting independently, able
/// to disagree with each other about the same boundary. See SPEC.md §17, the tenth question.
/// </para>
/// <para>
/// <b>It is authored by an operator, not by the agent it describes.</b> That is the whole
/// reason it is worth reading. A field a model could write would be exactly as trustworthy as
/// the prose it replaces, merely centralised; sourced from the launch environment, it is
/// declared by the same person in the same file that fixes the agent's name, and no operation
/// accepts one. An agent can no more edit its charter than its identity.
/// </para>
/// <para>
/// Advisory, and deliberately so. Nothing routes on it, nothing is refused because of it, and
/// two agents may claim the same ground without the broker saying which is right — principle
/// 2.6 leaves that to the people who own the repositories. It is a hint for choosing between
/// names, carrying no authority of its own.
/// </para>
/// </remarks>
public static class OwnershipStatement
{
    /// <summary>The longest statement the registry will hold.</summary>
    /// <remarks>
    /// Long enough for a domain and an explicit exclusion — <i>"Invoicing, tax and payment
    /// capture. Does not price orders or reserve stock."</i> is seventy-four characters — and
    /// short enough that it cannot become a description of a system.
    /// <para>
    /// The number came from measuring rather than from taste. A roster entry costs about
    /// seventy-five characters of structure before the statement, so ten peers at a hundred
    /// characters each cost roughly what one message is allowed to: finding out who to ask
    /// stays cheaper than asking. At a hundred and sixty, which is where this started, ten
    /// peers cost 2,352 characters and listing them cost more than a question.
    /// </para>
    /// </remarks>
    public const int MaxCharacters = 100;

    /// <summary>
    /// Reports whether a statement may be recorded, and why not when it may not.
    /// </summary>
    /// <param name="statement">The candidate, or <see langword="null"/> for none.</param>
    /// <param name="error">What is wrong with it, or <see langword="null"/> if nothing is.</param>
    /// <returns><see langword="true"/> if it may be recorded.</returns>
    /// <remarks>
    /// <para>
    /// The rule is that the statement renders as exactly what it says, on one line. This text is
    /// put into a list of peers that every agent reads, so anything able to break the line, hide
    /// itself, or reorder what follows it lets one agent's description of itself imitate the
    /// entries around it — a second agent, or an instruction, where there was neither.
    /// </para>
    /// <para>
    /// <b>Refused by Unicode category, because a control-character test is not the rule and was
    /// found not to be.</b> <c>char.IsControl</c> covers C0 and C1 and nothing else, so
    /// <c>U+2028 LINE SEPARATOR</c> and <c>U+2029 PARAGRAPH SEPARATOR</c> — which are line breaks
    /// wherever this text is rendered — went straight through it, as did
    /// <c>U+202E RIGHT-TO-LEFT OVERRIDE</c>, which reverses the display of everything after it,
    /// and <c>U+200B</c> and <c>U+FEFF</c>, which are invisible. Every one of those was admitted
    /// by the first version of this method and by the constraint behind it. So: no
    /// <see cref="UnicodeCategory.Control"/>, no
    /// <see cref="UnicodeCategory.Format"/>, and no line or paragraph
    /// separator. Ordinary spaces stay, and so does every letter, accent, dash and emoji in
    /// every script.
    /// </para>
    /// <para>
    /// Refused rather than stripped. Silently removing the separator would admit the intent and
    /// repair the symptom; refusing tells whoever wrote it that the field is one line.
    /// </para>
    /// <para>
    /// Returns rather than throws because both callers want the message: the server puts it on
    /// standard error and exits, and a test asserts on it.
    /// </para>
    /// </remarks>
    public static bool IsValid(string? statement, out string? error)
    {
        error = null;

        if (statement is null)
        {
            return true;
        }

        // Counted in text elements rather than UTF-16 units, so the number here is the number
        // the constraint behind it uses. Otherwise a statement of sixty astral characters is
        // reported as a hundred and twenty and refused for a length the database would not have
        // objected to, which sends whoever wrote it looking for the wrong problem.
        var length = statement.EnumerateRunes().Count();

        if (length > MaxCharacters)
        {
            error =
                $"it is {length} characters and the limit is {MaxCharacters}. It is read by " +
                "every agent that lists the roster, so say what you own and what you do not, " +
                "and nothing else.";

            return false;
        }

        foreach (var rune in statement.EnumerateRunes())
        {
            if (!Forbidden(Rune.GetUnicodeCategory(rune)))
            {
                continue;
            }

            error =
                $"it contains U+{rune.Value:X4}, which is not printable text. A statement is " +
                "one line and renders as what it says: it is put into a list of peers that " +
                "every agent reads, and anything that can break the line, hide itself, or " +
                "reorder what follows can imitate the entries around it.";

            return false;
        }

        return true;
    }

    /// <summary>Whether a category may not appear in a statement.</summary>
    /// <param name="category">The category of one character.</param>
    /// <returns><see langword="true"/> if it must be refused.</returns>
    /// <remarks>
    /// <para>
    /// Matched to the constraint in the schema, which refuses the same code points by range
    /// because SQLite has no notion of a category. The two must agree: the schema is the
    /// enforcement, and a value this method accepts and the database rejects would produce a
    /// server that starts and then fails its own registration.
    /// </para>
    /// <para>
    /// Applied to a rune rather than to a <see cref="char"/>, which is not a detail. Testing
    /// each UTF-16 unit puts both halves of a surrogate pair through this, and both are category
    /// <c>Surrogate</c> — so a version of this that refused surrogates refused every emoji and
    /// every character outside the basic plane. A well-formed rune is never a surrogate, so the
    /// category is simply absent from the list below.
    /// </para>
    /// </remarks>
    private static bool Forbidden(UnicodeCategory category) => category is
        UnicodeCategory.Control or
        UnicodeCategory.Format or
        UnicodeCategory.LineSeparator or
        UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// Reads a statement supplied by a launch, treating blank as absent.
    /// </summary>
    /// <param name="supplied">What the launch configuration carried, if anything.</param>
    /// <returns>The statement, trimmed, or <see langword="null"/> if there was none.</returns>
    /// <remarks>
    /// Blank collapses to absent rather than being stored, so a variable set to an empty string
    /// and a variable never set mean the same thing. The alternative records a peer as having
    /// declared that it owns nothing, which is a claim, and not the one that was made.
    /// </remarks>
    public static string? Read(string? supplied) =>
        string.IsNullOrWhiteSpace(supplied) ? null : supplied.Trim();
}
