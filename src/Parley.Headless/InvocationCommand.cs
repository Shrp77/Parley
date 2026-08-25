using System.Globalization;

namespace Parley.Headless;

/// <summary>
/// What to run to give an agent a turn, and where to run it.
/// </summary>
/// <param name="FileName">The executable.</param>
/// <param name="Arguments">Its arguments, already split.</param>
/// <param name="Timeout">How long to wait before giving up on the process.</param>
/// <remarks>
/// <para>
/// The command carries no question. That is the whole design and it is worth stating plainly,
/// because the obvious implementation is the wrong one: an invoker that passed the question in
/// would be relaying, and §8's rule 9 — refer, do not relay — exists because a relayed question
/// arrives under the relayer's identity with consequences that were not written for it. It would
/// also have to choose which of several owed questions to pass, and be wrong whenever there were
/// two.
/// </para>
/// <para>
/// So the invoker's only job is to give the model a turn. The agent then calls
/// <c>parley_inbox</c> through its own server, under its own identity, and sees exactly what it
/// would have seen had a person opened the session — which is what makes an invoked answer
/// indistinguishable from an attended one, and what keeps §13's trust boundary intact.
/// </para>
/// </remarks>
public sealed record InvocationCommand(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout)
{
    /// <summary>
    /// The prompt handed to an invoked agent.
    /// </summary>
    /// <remarks>
    /// Deliberately short, and deliberately not a description of the work. It names the tool to
    /// call and stops: the messages carry their own instructions, wrapped in the envelope of §13
    /// that marks them as a peer's words rather than the operator's. A prompt that summarised the
    /// waiting questions would be the operator paraphrasing one agent to another, outside that
    /// envelope and above it in authority.
    /// </remarks>
    public const string Prompt =
        "You have Parley messages waiting. Call parley_inbox, deal with what is there, and stop. " +
        "Do not start other work.";

    /// <summary>How long to wait for an invoked agent before giving up, when nothing says.</summary>
    /// <remarks>
    /// Long enough for a real answer and short enough that a wedged process is not held open for
    /// an afternoon. SPEC.md §4.2 expects an answer to take minutes and sometimes hours, but that
    /// is the *exchange's* budget, not one process's: an agent that has not finished in ten
    /// minutes has almost certainly stopped rather than slowed, and the exchange survives a
    /// timeout unharmed — it is still owed, and the next sweep tries again.
    /// </remarks>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Builds the command from a template written by an operator.
    /// </summary>
    /// <param name="template">The command line, with arguments separated by spaces.</param>
    /// <param name="timeout">How long to wait, or <see langword="null"/> for the default.</param>
    /// <returns>The parsed command.</returns>
    /// <exception cref="ArgumentException"><paramref name="template"/> is empty.</exception>
    /// <remarks>
    /// <para>
    /// Split on whitespace, honouring double quotes so a path containing spaces survives. It is
    /// not a shell: no pipes, no redirection, no variable expansion. That is a limitation and it
    /// is the safe direction — this string comes from a launch configuration, and a template
    /// interpreted by a shell would turn a typo in one into arbitrary command execution on the
    /// machine running the sweep.
    /// </para>
    /// <para>
    /// <see cref="Prompt"/> is appended as the final argument rather than interpolated into the
    /// template, so no template can place it somewhere it would be read as a flag.
    /// </para>
    /// </remarks>
    public static InvocationCommand Parse(string template, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var parts = Split(template);

        if (parts.Count == 0)
        {
            throw new ArgumentException(
                "An invocation command needs at least an executable.", nameof(template));
        }

        var fileName = parts[0];
        var arguments = parts.Skip(1).Append(Prompt).ToArray();

        return new InvocationCommand(fileName, arguments, timeout ?? DefaultTimeout);
    }

    /// <summary>Splits a command line on whitespace, keeping double-quoted runs together.</summary>
    /// <param name="template">The command line.</param>
    /// <returns>Its parts, with the quotes removed.</returns>
    private static List<string> Split(string template)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var c in template)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    /// <summary>Renders the command for a log or a refusal message.</summary>
    /// <returns>The executable and its arguments, quoted where they contain spaces.</returns>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Quote(FileName)} {string.Join(' ', Arguments.Select(Quote))}");

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? "\"" + value + "\"" : value;
}
