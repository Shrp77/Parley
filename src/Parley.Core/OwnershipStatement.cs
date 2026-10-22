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
/// <b>An agent may draft it; only a person may accept it.</b> SPEC.md §17 question 10 permits a
/// drafted line, because the half nobody else can supply is what an agent has decided it does
/// <em>not</em> own — a repository can be read to find out what it contains and cannot be read to
/// find out what it has ruled out.
/// </para>
/// <para>
/// <b>What makes it trustworthy:</b> no agent-facing operation writes this field, the registry is
/// filled from the launch environment on attach (§12.3), and which of three routes produced a
/// line — the agent's own words, its words edited, or a line a person composed with no ask — is
/// recorded against it. An agent can no more set its charter than its identity; it can be asked
/// what it should be.
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
    /// <para>
    /// <b>No bound, by default, and the largest integer is how that is spelled</b> — the same
    /// spelling <see cref="ExchangePolicy.Unbounded"/> and
    /// <see cref="ExchangePolicy.UnboundedClarifications"/> already use. Zero was not available: it
    /// would refuse every statement.
    /// </para>
    /// <para>
    /// <b>The absence of a bound is a recorded reduction in a control, not a tidy-up.</b> An agent is
    /// asked what it owns, and a two-part answer naming what it does not own and who it believes does
    /// will frequently not fit in a short bound. A cap censors the evidence that would justify it, but
    /// that precedent is weaker here, because this text is charged against every agent's context on
    /// every roster listing: a roster entry costs about seventy-five characters of structure before
    /// the statement. The constitution says that until the evidence exists a roster may be the largest
    /// thing an agent reads.
    /// </para>
    /// <para>
    /// <b>What holds is the character rule and the deployment's own bound.</b> Storage still holds a
    /// bound a deployment reinstates, so a number chosen from what agents actually propose can be put
    /// back the way the message cap's 8,000 was put in.
    /// </para>
    /// </remarks>
    public const int MaxCharacters = int.MaxValue;

    /// <summary>Whether a length bound is in force at all.</summary>
    /// <remarks>
    /// <b>Exists so that nothing has to print the sentinel.</b> A report says <i>"Caps in force: none"</i>
    /// rather than the number that spells <i>off</i>, because telling somebody a bound was set at
    /// 2,147,483,647 tells them a value nobody chose. Every surface that would otherwise name a limit asks this
    /// first, and a surface that names the number when this is <see langword="false"/> is a defect.
    /// </remarks>
    public static bool Bounded => MaxCharacters != int.MaxValue;

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
    /// <b>Refused by Unicode category, because a control-character test is not the rule.</b>
    /// <c>char.IsControl</c> covers C0 and C1 and nothing else, so
    /// <c>U+2028 LINE SEPARATOR</c> and <c>U+2029 PARAGRAPH SEPARATOR</c> — which are line breaks
    /// wherever this text is rendered — would go straight through it, as would
    /// <c>U+202E RIGHT-TO-LEFT OVERRIDE</c>, which reverses the display of everything after it,
    /// and <c>U+200B</c> and <c>U+FEFF</c>, which are invisible. So: no
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

        // Unreachable while no bound is in force, and kept rather than deleted: a deployment that
        // reinstates one gets this refusal back, naming the number actually in force. It can never
        // name the sentinel, because it cannot be reached when the sentinel is what is set.
        if (Bounded && length > MaxCharacters)
        {
            error =
                $"it is {length} characters and the limit is {MaxCharacters}. It is read by " +
                "every agent that lists the roster, so say what you own and what you do not, " +
                "and nothing else.";

            return false;
        }

        // **Counted as it goes, so the refusal can say where.** An agent answers in two halves, and
        // "it contains U+2028" is not actionable advice about a paragraph. The position is in text
        // elements, like the length above, so the two numbers a person reads are counted the same way.
        var at = 0;

        foreach (var rune in statement.EnumerateRunes())
        {
            at++;

            if (!Forbidden(Rune.GetUnicodeCategory(rune)))
            {
                continue;
            }

            error = string.Create(
                CultureInfo.InvariantCulture,
                $"it contains U+{rune.Value:X4} at position {at}, which is not printable text. A " +
                $"statement is one line and renders as what it says: it is put into a list of peers " +
                $"that every agent reads, and anything that can break the line, hide itself, or " +
                $"reorder what follows can imitate the entries around it.");

            return false;
        }

        return true;
    }

    /// <summary>
    /// An answer as a line: every run of whitespace, line breaks included, becomes one space, and the ends are
    /// trimmed (SPEC.md §10.2).
    /// </summary>
    /// <param name="answer">What the agent wrote.</param>
    /// <returns>The same words on one line.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why there is a flatten at all.</b> A model answers in paragraphs, and a statement refuses a line break
    /// (<see cref="IsValid"/>). Without this, Auto Accept would almost never accept anything.
    /// </para>
    /// <para>
    /// <b>It changes no word, and that is the whole of what keeps <see cref="OwnershipSource.Automatic"/>
    /// truthful.</b> Only whitespace moves. A format or control character that is not whitespace is left exactly
    /// where it was, so <see cref="IsValid"/> still refuses it and the answer waits for a person — anything that
    /// would change what the agent said is a person's decision, recorded as edited.
    /// </para>
    /// </remarks>
    public static string Flatten(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var line = new StringBuilder(answer.Length);
        var space = false;

        foreach (var rune in answer.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                space = line.Length > 0;
                continue;
            }

            if (space)
            {
                line.Append(' ');
                space = false;
            }

            line.Append(rune.ToString());
        }

        return line.ToString();
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
