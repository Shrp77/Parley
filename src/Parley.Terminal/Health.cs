using System.Collections.ObjectModel;
using System.Globalization;
using Parley.Client.Rendering;
using Parley.Core;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// What needs the person running a project, and what its work came to: the console's Health page, in a terminal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Five reports of §10.2 in one read-only dialog, because they answer one question.</b> Nothing in the
/// protocol times out (principle 2.8), so stalled work ends only when a person sees it and acts — and this is
/// where they see it. The console gathers the same five on one page, and a terminal that offered fewer would be
/// the surface a person at the end of an SSH session cannot learn the state of their deployment from.
/// </para>
/// <para>
/// <b>The column-zero rule of <see cref="TranscriptRegion"/> applies here.</b> Agent names, conversation
/// identifiers and responder identities are all somebody else's characters, so a heading sits at column zero,
/// every row is indented, and everything drawn goes through <see cref="Safe.Text"/>.
/// </para>
/// </remarks>
internal sealed class Health : Dialog
{
    private readonly ObservableCollection<string> _lines = [];

    private Health(string project, IReadOnlyList<string> lines)
    {
        HotKeySpecifier = Plain.NoHotKey;
        Title = "Health of " + Safe.Text(project);
        Width = Dim.Percent(90);
        Height = Dim.Percent(80);

        foreach (var line in lines)
        {
            _lines.Add(line);
        }

        var list = Lists.Filling();

        list.SetSource(_lines);

        Add(list);
    }

    /// <summary>Every line this dialog shows.</summary>
    internal IReadOnlyList<string> Shown => _lines;

    /// <summary>Turns the five reports into lines, headings at column zero and nothing else.</summary>
    /// <param name="flags">Open work that needs the person.</param>
    /// <param name="waiting">Who is waiting on whom.</param>
    /// <param name="results">What Results reported.</param>
    /// <param name="refusals">What the size cap refused.</param>
    /// <param name="clarifications">What clarification cost.</param>
    /// <returns>The lines.</returns>
    /// <remarks>
    /// Flags first, because they are the part that asks for something. Counts and kinds throughout and never a
    /// word of what anybody said: the reader is outside the §13 envelope.
    /// </remarks>
    internal static IReadOnlyList<string> Lines(
        FlagReport flags,
        IReadOnlyList<WaitingEdge> waiting,
        ResultsReport results,
        IReadOnlyList<SizeRefusal> refusals,
        ClarifyReport clarifications)
    {
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(waiting);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(refusals);
        ArgumentNullException.ThrowIfNull(clarifications);

        var lines = new List<string> { "Work that needs you" };

        if (flags.IsEmpty)
        {
            lines.Add("  Nothing is flagged.");
        }

        foreach (var flag in flags.Exchanges)
        {
            lines.Add("  " + Safe.Text(Describe(flag)));
        }

        if (flags.Cycles.Count > 0)
        {
            lines.Add("  Requests that went round in a circle:");

            foreach (var cycle in flags.Cycles)
            {
                lines.Add("    " + Safe.Text(
                    $"{string.Join(" -> ", cycle.Path.Select(a => a.Value))} in {cycle.ConversationId}"));
            }
        }

        lines.Add(string.Empty);
        lines.Add("Who is waiting on whom");

        if (waiting.Count == 0)
        {
            lines.Add("  Nobody is waiting on a result.");
        }

        foreach (var edge in waiting)
        {
            lines.Add("  " + Safe.Text(
                string.Create(CultureInfo.InvariantCulture, $"{edge.Waiter.Value} is waiting on {edge.WaitedOn.Value} (exchange {edge.ExchangeId})")));
        }

        lines.Add(string.Empty);
        lines.Add("What results reported");

        if (results.IsEmpty)
        {
            lines.Add("  No results have been sent yet.");
        }

        foreach (var row in results.Outcomes)
        {
            lines.Add("  " + Safe.Text(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.Agent}: {row.Items} {OutcomeTokens.Format(row.Outcome)} (responder {row.ResponderModel})")));
        }

        foreach (var row in results.Blockers)
        {
            lines.Add("  " + Safe.Text(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.Agent}: {row.Items} blocked by {OutcomeTokens.Format(row.Kind).Replace('_', ' ')}")));
        }

        foreach (var row in results.Sources)
        {
            lines.Add("  " + Safe.Text(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.Agent} relied on {row.Source} for {row.Items} item{(row.Items == 1 ? string.Empty : "s")}")));
        }

        foreach (var row in results.Closures)
        {
            lines.Add("  " + Safe.Text(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.Agent}: {row.ByConsensus} closed by agreement, {row.ByPerson} closed by a person")));
        }

        lines.Add(string.Empty);
        lines.Add("What the size cap refused");

        if (refusals.Count == 0)
        {
            lines.Add("  Nothing has been refused for being too long.");
        }

        foreach (var refusal in refusals)
        {
            lines.Add("  " + Safe.Text(string.Create(
                CultureInfo.InvariantCulture,
                $"{refusal.Agent.Value}: {refusal.Refusals} {MessageIdentity.FormatType(refusal.Type)} refused, "
                + $"largest {refusal.Worst} characters against a cap of {refusal.Cap}")));
        }

        lines.Add(string.Empty);
        lines.Add("What clarification cost");

        lines.Add(clarifications.Exchanges > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"  {clarifications.Exchanges} exchanges, the deepest reaching {clarifications.DeepestReached} rounds; "
                + $"{clarifications.ExchangesAtCap} ran out of rounds. {clarifications.Results} results, "
                + $"{clarifications.ResultsWithMixedOutcomes} with mixed outcomes.")
            : "  Nothing has been clarified.");

        return lines;
    }

    /// <summary>One flag, in the console's words.</summary>
    /// <param name="flag">The flag.</param>
    /// <returns>The sentence.</returns>
    private static string Describe(Flag flag)
    {
        var since = LocalTime.Write(flag.SinceUtc);
        // The sentence is the one every surface uses (`FlagWording`); the exchange is this dialog's, for finding it. A vote
        // names its conversation in the sentence already.
        var where = flag.Kind == FlagKind.NotVoted
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $" ({flag.ConversationId}, exchange {flag.ExchangeId})");

        return FlagWording.Describe(flag, since) + where;
    }

    /// <summary>Shows a project's health.</summary>
    /// <param name="app">The application.</param>
    /// <param name="project">Which project.</param>
    /// <param name="lines">What <see cref="Lines"/> made of its reports.</param>
    /// <returns>The dialog and its session.</returns>
    internal static (Health View, SessionToken? Token) Open(IApplication app, string project, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(app);

        var view = new Health(project, lines);

        return (view, Prompting.Open(app, view));
    }
}
