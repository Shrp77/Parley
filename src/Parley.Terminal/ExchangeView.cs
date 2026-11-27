using System.Collections.ObjectModel;
using Parley.Client.Rendering;
using Parley.Client.Seating;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;

namespace Parley.Terminal;

/// <summary>The exchanges a message names, opened from where it names them (spec 036, FR-025).</summary>
/// <remarks>
/// <para>
/// <b>The terminal's half of the console's panel</b>, from the same read (<see cref="ExchangeDetail"/>): each exchange's
/// state and every message on it the store still holds. Read-only, and reading it changes nothing in the broker.
/// </para>
/// <para>
/// Every line is passed through <see cref="Safe.Text"/>: a message is somebody else's words.
/// </para>
/// </remarks>
internal sealed class ExchangeView : TableDialog<ExchangeView>, IKeyTable<ExchangeView>
{
    /// <summary>What the dialog is called.</summary>
    internal const string Heading = "Exchanges this message names";

    private readonly ObservableCollection<string> _lines = [];

    internal ExchangeView(IReadOnlyList<ExchangeDetail> details)
    {
        HotKeySpecifier = Plain.NoHotKey;
        Title = Heading;
        Width = Dim.Percent(90);
        Height = Dim.Percent(80);

        foreach (var line in Lines(details))
        {
            _lines.Add(line);
        }

        var list = Lists.Filling();

        list.SetSource(_lines);

        Add(list);
    }

    /// <summary>Every line this dialog shows.</summary>
    internal IReadOnlyList<string> Shown => _lines;

    /// <summary>The lines for some exchanges: a heading and state for each, then each message on it.</summary>
    /// <param name="details">The exchanges.</param>
    /// <returns>The lines, each made inert.</returns>
    internal static IReadOnlyList<string> Lines(IReadOnlyList<ExchangeDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);

        var lines = new List<string>();

        foreach (var detail in details)
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            if (!detail.Found)
            {
                lines.Add($"There is no exchange #{detail.Exchange} in this project.");
                continue;
            }

            lines.Add(Safe.Text($"Exchange #{detail.Exchange} in {detail.Conversation}. {detail.State(detail.Ending())}"));

            if (detail.Messages.Count == 0)
            {
                lines.Add("  Its words were removed by retention.");
            }

            foreach (var message in detail.Messages)
            {
                lines.Add(Safe.Text($"  {message.From.Value} to {message.To.Value}, {message.Type}, {LocalTime.Clock(message.CreatedUtc)}:"));

                var payload = Payload.Read(message.PayloadJson);
                var text = payload.Raw ?? string.Join(
                    "\n",
                    new[] { payload.Said }.Concat(payload.Points.Select(point =>
                        $"{point.Id}: {point.Text}{(point.Outcome is { Length: > 0 } outcome ? $" ({outcome})" : string.Empty)}"))
                        .Where(part => !string.IsNullOrEmpty(part)));

                lines.AddRange(text.ReplaceLineEndings("\n").Split('\n').Select(row => "    " + Safe.Text(row)));
            }
        }

        return lines;
    }

    /// <inheritdoc/>
    /// <remarks>No command of its own: read-only. Escape leaves through the toolkit; the lines take their own keys first.</remarks>
    static CommandManager<ExchangeView> IKeyTable<ExchangeView>.BuildTable() => new(
        [new(KeyContext.ExchangeDialog, [])],
        [],
        [
            .. ReservedKey.Moving(KeyContext.ExchangeDialog),
            .. ReservedKey.TakenBy(KeyContext.ExchangeDialog, () => Lists.Filling(), "the lines"),
        ]);

    /// <summary>Shows some exchanges.</summary>
    /// <param name="app">The application.</param>
    /// <param name="details">The exchanges.</param>
    /// <returns>The dialog and its session.</returns>
    internal static (ExchangeView View, SessionToken? Token) Open(IApplication app, IReadOnlyList<ExchangeDetail> details)
    {
        ArgumentNullException.ThrowIfNull(app);

        var view = new ExchangeView(details);

        return (view, Prompting.Open(app, view));
    }
}
