namespace Parley.Core;

/// <summary>
/// The rules a roadmap's text must meet, applied wherever it is written (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>No length bound, and one rule that is not relaxed.</b> A proposal, its title and its items have no length
/// limit, as an agent's ownership line has none (SPEC.md §16, constitution 6.0.0): a person reads them, and what
/// reaches an agent is one item inside a Request, which keeps the message cap. What stays is the rule that text
/// renders as exactly what it says, on one line. A roadmap is drawn by the terminal and the web console, and
/// anything able to break a line, hide itself or reorder what follows lets an agent imitate a row it did not
/// write — so the character rule is the ownership statement's, reused rather than copied, and a change to one is
/// a change to both.
/// </para>
/// <para>
/// <b>Guidance, not enforcement, for brevity.</b> A short, point-oriented item is what the proposing tool asks
/// for. Nothing here checks it, because the person is the control and a check would refuse the one proposal a
/// person wanted anyway.
/// </para>
/// </remarks>
public static class RoadmapText
{
    /// <summary>Reports whether a title or an item may be recorded, and why not when it may not.</summary>
    /// <param name="text">The candidate.</param>
    /// <param name="what">What it is, in a sentence's own words, for example <c>an item</c>.</param>
    /// <param name="error">What is wrong with it, or <see langword="null"/> if nothing is.</param>
    /// <returns><see langword="true"/> if it may be recorded.</returns>
    public static bool IsValid(string? text, string what, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);

        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = $"{what} is empty. It is one line saying what is to be done.";

            return false;
        }

        // The decision is the ownership statement's; the sentence is this field's own, since that one speaks of
        // a roster that nobody reading a roadmap is looking at.
        if (!OwnershipStatement.IsValid(text, out _))
        {
            error = $"{what} holds a line break or a character that is not printable text. A roadmap's text " +
                "is one line and is drawn as exactly what it says, so anything that could break the line, hide " +
                "itself or reorder what follows is refused.";

            return false;
        }

        return true;
    }
}
