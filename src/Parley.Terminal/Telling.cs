using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Parley.Client.Rendering;
using Parley.Core;

namespace Parley.Terminal;

/// <summary>
/// What an operator told one agent, and what it said back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and the terminal cannot issue an instruction at all.</b> That is not an oversight
/// being papered over: instructing is the one operation that puts a person's words into an agent's
/// turn (SPEC.md §10.2), the console has a composer for it, and adding a second way to do it here
/// was not asked for. What was missing on both surfaces was the ability to find out what became of
/// one, and that is what this is.
/// </para>
/// <para>
/// <b>A turn can outlast the call that asked for it.</b> A browser holding a request open for
/// twenty minutes is the defect this feature exists to fix, so an instruction is issued, the caller
/// is told it is running, and the answer is collected later — from here, or from the console, or
/// from <c>parley instructions</c>. Which is why this exists on the surface that does not issue
/// them: somebody at a terminal over SSH is exactly the person who cannot use the browser one.
/// </para>
/// <para>
/// <b>The column-zero rule of <see cref="TranscriptRegion"/> applies here for the same reason.</b>
/// A reply is agent-written, arrives from the far end of a gRPC call, and may carry line breaks —
/// so a reply left unindented could be shaped exactly like this dialog's own heading line and
/// present an instruction nobody gave. A heading sits at column zero and nothing else does.
/// </para>
/// </remarks>
internal sealed class Telling : Dialog
{
    /// <summary>What is said when the operator has told this agent nothing.</summary>
    internal const string Nothing = "Nothing has been said to this agent.";

    /// <summary>What a turn still running is called, in the one word that is true of it.</summary>
    /// <remarks>
    /// Deliberately not "failed" and deliberately not blank. §10.2 defines <c>failed</c> as a turn
    /// attempted and not completed, which nobody may claim while it is still being attempted; and a
    /// blank outcome reads as a failure to anybody scanning a column.
    /// </remarks>
    internal const string Running = "still running";

    /// <summary>What a refusal is called, which is not a failure.</summary>
    /// <remarks>
    /// §11.5 says an agent may decline because the operator is a person rather than a superior
    /// process. §10.2 requires the two told apart, because an interface that renders refusal as
    /// malfunction teaches whoever reads it to work around the agent instead of asking why.
    /// </remarks>
    internal const string Declined = "declined, which is its right";

    private readonly ObservableCollection<string> _lines = [];

    private Telling(AgentName agent, IReadOnlyList<InstructionRecord> instructions)
    {
        Title = "What you have told " + agent.Value;
        Width = Dim.Percent(90);
        Height = Dim.Percent(80);

        foreach (var line in Lines(instructions))
        {
            _lines.Add(line);
        }

        var list = Lists.Filling();

        list.SetSource(_lines);

        Add(list);
    }

    /// <summary>Every line this dialog shows.</summary>
    internal IReadOnlyList<string> Shown => _lines;

    /// <summary>Turns instructions into lines, headings at column zero and nothing else.</summary>
    /// <param name="instructions">What the operator said, newest first.</param>
    /// <returns>The lines.</returns>
    /// <remarks>
    /// <para>
    /// Two lines per instruction at least: a heading carrying when and what became of it, then the
    /// words, indented. The reply is indented too and is flattened onto one line — the same
    /// defence the conversation export uses, and for the same reason: a payload carrying a newline
    /// and then a plausible heading is a forgery a reader cannot see through.
    /// </para>
    /// <para>
    /// Neutralised through <see cref="Safe.Text"/> like everything else drawn here. Both the text
    /// and the reply are somebody else's characters — the reply an agent's, the text a person's,
    /// and a person can paste an escape sequence as easily as an agent can emit one.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> Lines(IReadOnlyList<InstructionRecord> instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);

        if (instructions.Count == 0)
        {
            return [Nothing];
        }

        var lines = new List<string>();

        foreach (var instruction in instructions)
        {
            lines.Add(Safe.Text(
                LocalTime.Write(instruction.IssuedUtc) + "  " + Became(instruction)));

            lines.Add("  " + Safe.Text(Flat(instruction.Text)));

            if (instruction.Reply is { Length: > 0 } said)
            {
                lines.Add("  > " + Safe.Text(Flat(said)));
            }

            lines.Add(string.Empty);
        }

        return lines;
    }

    /// <summary>What became of one instruction, in words.</summary>
    /// <param name="instruction">The instruction.</param>
    /// <returns>The words.</returns>
    private static string Became(InstructionRecord instruction) => instruction.Outcome switch
    {
        InstructionOutcome.Pending => Running,
        InstructionOutcome.Declined => Declined,
        _ => instruction.Outcome.ToString().ToLowerInvariant()
    };

    /// <summary>Puts text on one line, so nothing it carries can be shaped like a heading.</summary>
    /// <param name="text">The text.</param>
    /// <returns>One line.</returns>
    private static string Flat(string text) => text.ReplaceLineEndings(" ");

    /// <summary>Shows what an operator told one agent.</summary>
    /// <param name="app">The application.</param>
    /// <param name="agent">Whose instructions.</param>
    /// <param name="instructions">What was said, newest first.</param>
    /// <returns>The dialog and its session.</returns>
    internal static (Telling View, SessionToken? Token) Open(
        IApplication app,
        AgentName agent,
        IReadOnlyList<InstructionRecord> instructions)
    {
        ArgumentNullException.ThrowIfNull(app);

        var view = new Telling(agent, instructions);

        return (view, Prompting.Open(app, view));
    }
}
