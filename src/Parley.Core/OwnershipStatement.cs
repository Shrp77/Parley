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
    /// Control characters are refused rather than stripped, and the line break is the one that
    /// matters. This text is rendered into a list of peers that every agent reads, so a
    /// statement spanning several lines could imitate the structure around it and appear to be
    /// a further entry, or an instruction, rather than one agent's description of itself.
    /// Silently removing the newline would admit the intent and repair the symptom; refusing
    /// tells whoever wrote it that the field is one line.
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

        if (statement.Length > MaxCharacters)
        {
            error =
                $"it is {statement.Length} characters and the limit is {MaxCharacters}. It is " +
                "read by every agent that lists the roster, so say what you own and what you " +
                "do not, and nothing else.";

            return false;
        }

        foreach (var character in statement)
        {
            if (!char.IsControl(character))
            {
                continue;
            }

            error =
                "it contains a control character. A statement is one line: it is rendered into " +
                "a list of peers that every agent reads, and one that spans lines can imitate " +
                "the entries around it.";

            return false;
        }

        return true;
    }

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
