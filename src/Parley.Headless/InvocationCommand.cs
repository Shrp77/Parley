using System.Globalization;
using Parley.Core;

namespace Parley.Headless;

/// <summary>
/// What to run to keep an agent resident, and where to run it.
/// </summary>
/// <param name="FileName">The executable.</param>
/// <param name="Arguments">Its arguments, already split.</param>
/// <param name="Timeout">How long to wait for one turn before giving up on it.</param>
/// <remarks>
/// <para>
/// The command carries no Request. The supervisor delegates a turn, never a judgement (SPEC.md §11.1):
/// a fleet that passed the work in would put it under the supervisor's identity instead of the
/// requester's, and would have to choose which of several waiting Requests to pass.
/// </para>
/// <para>
/// So the fleet's only job is to give the agent a turn. The agent then calls <c>parley_inbox</c>
/// through its own server, under its own identity, and sees exactly what it would have seen had a
/// person opened the session — which is what makes unattended work indistinguishable from attended
/// work, and what keeps §13's trust boundary intact.
/// </para>
/// </remarks>
public sealed record InvocationCommand(
    string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout)
{
    /// <summary>
    /// The text that gives a resident agent a turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A turn is for doing the work</b> (§11.1). The prompt this replaced told the agent to deal with
    /// its mail, end its turn and not start unrelated work — and an agent handed a request for work in its
    /// repository read that as licence to collect it and stop. A runner that tells an agent to collect and
    /// stop contradicts principle 2.1 and is a defect in the runner.
    /// </para>
    /// <para>
    /// It still does not describe the work: the messages carry their own, inside the envelope of §13 that
    /// marks them as another participant's words. A prompt that summarised them would be the operator
    /// paraphrasing one agent to another, outside that envelope and above it in authority.
    /// </para>
    /// </remarks>
    public const string Prompt =
        "Parley work is waiting for you. Call parley_inbox and act on what is there: do the work you " +
        "are asked for in this repository, or resolve what stops it, and send a parley_result for each " +
        "request. Pursue what you need from other agents with parley_request. When nothing you were " +
        "asked for is left to do this turn, end it.";

    /// <summary>
    /// The arguments that make a runner speak the fleet's turn protocol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one vendor-specific thing in this project, isolated here on purpose.</b> A resident
    /// agent is a process the fleet writes turns to and reads results from, and that requires
    /// agreeing on a wire format with whatever is on the other end. These are Claude Code's
    /// flags for it: a printing session that reads newline-delimited JSON turns from standard
    /// input and writes newline-delimited JSON events back.
    /// </para>
    /// <para>
    /// They are appended rather than required, and skipped entirely when a template names its own
    /// <c>--input-format</c>. A different runner therefore needs a different launch
    /// configuration and no change to this code, which is the standing constraint that the
    /// transport is disposable applied to the one place a vendor leaked in.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ProtocolArguments { get; } =
    [
        "-p",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose"
    ];

    /// <summary>
    /// The arguments that make a resident session identifiable as the agent it is.
    /// </summary>
    /// <param name="agent">The registry name.</param>
    /// <returns>The runner's naming flag and its value.</returns>
    /// <remarks>
    /// <para>
    /// Supplied by the fleet from the registry rather than written into each launch
    /// configuration. The name already exists there as <c>PARLEY_AGENT</c>, and a second copy in
    /// a <c>-n</c> flag beside it is a copy that can disagree — at which point the session
    /// listing names one agent and the protocol names another, and the listing is the one a
    /// person reads.
    /// </para>
    /// <para>
    /// Worth almost nothing under the one-shot form this replaces, which is why it was passed
    /// over then: a printing session that exits has no prompt box and no terminal to title. A
    /// resident agent persists, so its session is one an operator will later open by hand, and
    /// nineteen of them listed by first line rather than by name is a list nobody can use.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> NamingArguments(string agent) => ["-n", agent];

    /// <summary>How long to wait for one turn, when nothing says.</summary>
    /// <remarks>
    /// <para>
    /// Generous, and it can afford to be now. Under the one-shot invoker a timeout killed the
    /// process, so waiting too long held a wedged agent open for an afternoon. A resident agent
    /// survives its own timeouts, so the cost of waiting is small and the cost of giving up early
    /// is real: an abandoned turn leaves a reply owed to nobody, which has to be discarded before
    /// the next turn can trust what it reads.
    /// </para>
    /// <para>
    /// <b>Taken from <see cref="TurnBudget.Longest"/> rather than written here, because it was
    /// written here and the broker wrote a different one.</b> The broker waited five minutes for an
    /// instruction and this allowed twenty, so every turn between the two was settled failed while
    /// the agent was still working and its real answer refused on arrival. Neither number was wrong
    /// alone; nothing compared them. The value is unchanged — what changed is that there is now one
    /// of it.
    /// </para>
    /// </remarks>
    public static TimeSpan DefaultTimeout { get; } = TurnBudget.Longest;

    /// <summary>
    /// Builds the command from a template written by an operator.
    /// </summary>
    /// <param name="template">The command line, with arguments separated by spaces.</param>
    /// <param name="agent">
    /// The registry name to label the session with, or <see langword="null"/> to leave it unnamed.
    /// </param>
    /// <param name="timeout">How long to wait for a turn, or <see langword="null"/> for the default.</param>
    /// <returns>The parsed command.</returns>
    /// <exception cref="ArgumentException"><paramref name="template"/> is empty.</exception>
    /// <remarks>
    /// <para>
    /// Split on whitespace, honouring double quotes so a path containing spaces survives. It is
    /// not a shell: no pipes, no redirection, no variable expansion. That is a limitation and it
    /// is the safe direction — this string comes from a launch configuration, and a template
    /// interpreted by a shell would turn a typo in one into arbitrary command execution on the
    /// machine running the fleet.
    /// </para>
    /// <para>
    /// <see cref="ProtocolArguments"/> is appended rather than interpolated, so no template can
    /// place a protocol flag somewhere it would be read as a value, and no template can quietly
    /// omit one and leave the fleet talking to a process that cannot answer.
    /// </para>
    /// </remarks>
    public static InvocationCommand Parse(
        string template, string? agent = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var parts = Split(template);

        if (parts.Count == 0)
        {
            throw new ArgumentException(
                "An invocation command needs at least an executable.", nameof(template));
        }

        var fileName = parts[0];
        var given = parts.Skip(1).ToList();

        // A template that names its own input format is configuring a different runner, and
        // appending Claude Code's flags to it would produce a command line that means nothing to
        // either. Taking the template at its word is the escape hatch, and it covers the naming
        // flag too: `-n` is this runner's spelling, not a universal one.
        if (given.Contains("--input-format", StringComparer.Ordinal))
        {
            return new InvocationCommand(fileName, given, timeout ?? DefaultTimeout);
        }

        // An operator who named the session themselves meant it. Appending a second `-n` would
        // leave the runner to pick between them, which is a coin toss written as configuration.
        var named =
            agent is { Length: > 0 } &&
            !given.Contains("-n", StringComparer.Ordinal) &&
            !given.Contains("--name", StringComparer.Ordinal);

        List<string> arguments = named
            ? [.. given, .. NamingArguments(agent!), .. ProtocolArguments]
            : [.. given, .. ProtocolArguments];

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
