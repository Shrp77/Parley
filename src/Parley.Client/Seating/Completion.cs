namespace Parley.Client.Seating;

/// <summary>Which sigil opened a completion list, and so what it offers.</summary>
public enum Sigil
{
    /// <summary>None: the text does not end in something being completed.</summary>
    None = 0,

    /// <summary><c>/</c>, over the commands.</summary>
    Command = 1,

    /// <summary><c>@</c>, over the agents.</summary>
    Agent = 2
}

/// <summary>
/// The narrowing list behind both sigils.
/// </summary>
/// <remarks>
/// <para>
/// <b>One mechanism over two candidate sets.</b> <c>/</c> offers the commands and <c>@</c> offers the agents, and
/// they differ in nothing else — same narrowing, same keys, same behaviour when nothing matches. Two lists built
/// separately would eventually behave differently in the same input line, which is the worst place for two
/// behaviours.
/// </para>
/// <para>
/// <b>Derived from the text, never remembered.</b> There is no flag saying somebody was completing something:
/// what the list shows is a function of what is written, which is the only way deleting back into a match can
/// bring the list back. A dismissed flag would be 0.29.1's cached waiting count in a new place — a number that
/// only ever climbed because a notice arrives when something lands and never when it is collected.
/// </para>
/// <para>
/// <b>Prefix fills the list; an exact identifier decides the send.</b> Those are two different comparisons and
/// this type only does the first. <c>/ad</c> matches <c>add</c> here and is still text, because nothing completed
/// it — see <see cref="Commands.Resolve"/>.
/// </para>
/// </remarks>
public sealed record Completion
{
    private Completion(Sigil sigil, string typed, IReadOnlyList<string> matches)
    {
        Which = sigil;
        Typed = typed;
        Matches = matches;
    }

    /// <summary>Which sigil is being completed, or none.</summary>
    public Sigil Which { get; }

    /// <summary>What follows the sigil so far.</summary>
    public string Typed { get; init; } = string.Empty;

    /// <summary>The candidates that still begin with what is typed, in the order they were offered.</summary>
    public IReadOnlyList<string> Matches { get; init; } = [];

    /// <summary>Whether a list is open. Nothing matching is a closed list, not an empty one.</summary>
    public bool Open => Matches.Count > 0;

    /// <summary>Which match is highlighted.</summary>
    public int Selected { get; init; }

    /// <summary>Reads what the text is completing, if anything.</summary>
    /// <param name="text">The whole line, as typed.</param>
    /// <param name="commands">Every command identifier.</param>
    /// <param name="agents">Every agent that could be addressed.</param>
    /// <param name="selected">Which match was highlighted before, kept where it still exists.</param>
    /// <returns>The list, which may be closed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="commands"/> or <paramref name="agents"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// A command completes only at the start of the line, because that is what makes a command a command
    /// (§5.1's ask is otherwise indistinguishable from one). An agent completes wherever <c>@</c> was typed, since
    /// addressing is something a person does part way through a sentence.
    /// </para>
    /// <para>
    /// A sigil followed by a space is finished: what comes after it is a parameter or prose, not more of the
    /// identifier. That is what keeps <c>/etc/hosts</c> and a sentence containing an email address out of the list.
    /// </para>
    /// </remarks>
    public static Completion Read(
        string? text,
        IReadOnlyList<string> commands,
        IReadOnlyList<string> agents,
        int selected = 0)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(agents);

        var line = text ?? string.Empty;

        if (line.StartsWith('/') && Completing(line[1..]) is { } command)
        {
            return Narrowed(Sigil.Command, command, commands, selected);
        }

        var at = line.LastIndexOf('@');

        if (at >= 0 && Completing(line[(at + 1)..]) is { } agent)
        {
            return Narrowed(Sigil.Agent, agent, agents, selected);
        }

        return new Completion(Sigil.None, string.Empty, []);
    }

    /// <summary>What Tab would put in place of what is typed.</summary>
    /// <returns>The selected match, or null when the list is closed.</returns>
    public string? Completes() => Open ? Matches[Math.Clamp(Selected, 0, Matches.Count - 1)] : null;

    /// <summary>The line with the selection in place of what was typed.</summary>
    /// <param name="text">The line the completion was read from.</param>
    /// <returns>The line completed, or unchanged where there is nothing to complete.</returns>
    /// <remarks>
    /// <para>
    /// <b>One rule over both sigils</b>, for the reason the narrowing is one: what was typed is the tail of the
    /// line, so the sigil is at <c>Length - Typed.Length - 1</c> whichever sigil it is. A second implementation
    /// per sigil would be two ways to edit one input line.
    /// </para>
    /// <para>
    /// A trailing space, because a sigil followed by a space is finished (<see cref="Read"/>) — so completing
    /// closes the list rather than leaving it open over a match it has already made.
    /// </para>
    /// <para>
    /// It refuses a line that is no longer the one it read. The completion is derived on every keystroke and
    /// consumed at once, so this cannot normally happen; what it must never do is splice into a different line,
    /// which would rewrite words nobody asked it to touch.
    /// </para>
    /// </remarks>
    public string Fills(string? text)
    {
        var line = text ?? string.Empty;

        if (Completes() is not { } match || line.Length < Typed.Length + 1)
        {
            return line;
        }

        var before = line[..(line.Length - Typed.Length)];

        return line.EndsWith(Typed, StringComparison.Ordinal) ? before + match + " " : line;
    }

    /// <summary>Moves the highlight, wrapping at each end.</summary>
    /// <param name="by">How far, and which way.</param>
    /// <returns>The list with the highlight moved.</returns>
    /// <remarks>
    /// Wrapping rather than stopping, because a list of two is faster to cycle than to aim at — and 0.20.3
    /// records what a list that cannot be left in one direction costs.
    /// </remarks>
    public Completion Move(int by) => Open
        ? this with { Selected = ((Selected + by) % Matches.Count + Matches.Count) % Matches.Count }
        : this;

    /// <summary>Whether what follows a sigil is still being typed, rather than finished.</summary>
    /// <param name="rest">Everything after the sigil.</param>
    /// <returns>What has been typed, or null when this is not a completion at all.</returns>
    private static string? Completing(string rest) =>
        rest.Contains(' ', StringComparison.Ordinal) || rest.Contains('/', StringComparison.Ordinal)
            ? null
            : rest;

    /// <summary>The candidates that begin with what is typed.</summary>
    /// <param name="sigil">Which sigil.</param>
    /// <param name="typed">What follows it.</param>
    /// <param name="candidates">What it offers.</param>
    /// <param name="selected">The highlight to keep where it still exists.</param>
    /// <returns>The narrowed list.</returns>
    private static Completion Narrowed(
        Sigil sigil,
        string typed,
        IReadOnlyList<string> candidates,
        int selected)
    {
        var matches = candidates
            .Where(c => c.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new Completion(sigil, typed, matches)
        {
            Selected = matches.Count == 0 ? 0 : Math.Clamp(selected, 0, matches.Count - 1),
        };
    }
}
