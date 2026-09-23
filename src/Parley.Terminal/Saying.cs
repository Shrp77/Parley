using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// The last thing this surface said, whole, when the message line could not hold all of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The message line expands and is still bounded, so this is where the rest lives.</b> A
/// broadcast to nineteen agents reports a count, a cause and the names it was refused for, and the
/// cause is whatever came back from the far end of a gRPC call — so the one row the line used to
/// be was cut off mid-sentence, which the owner met and reported. The line grows to a paragraph
/// now; past that it says how much more there is and names this.
/// </para>
/// <para>
/// A list rather than a label, because a label is sized to what it is given and this is given
/// whatever a broker said. The list scrolls, so nothing here depends on the message being short.
/// </para>
/// <para>
/// Wrapped by <see cref="TranscriptRegion.Wrap"/>, the one wrapper this surface has, and every row
/// is already inert: <see cref="Shell.Said"/> neutralises on the way in, which is what makes the
/// breaks inserted here the surface's own rather than something a payload chose.
/// </para>
/// </remarks>
internal sealed class Saying : Dialog
{
    /// <summary>What the dialog is called.</summary>
    internal const string Heading = "The whole message";

    private readonly ObservableCollection<string> _lines = [];

    private Saying(string said, int width)
    {
        Title = Heading;
        Width = Dim.Percent(90);
        Height = Dim.Percent(80);

        foreach (var line in TranscriptRegion.Wrap(said, width))
        {
            _lines.Add(line);
        }

        var list = Lists.Filling();

        list.SetSource(_lines);

        Add(list);
    }

    /// <summary>Every line this dialog shows.</summary>
    internal IReadOnlyList<string> Shown => _lines;

    /// <summary>Shows a message whole.</summary>
    /// <param name="app">The application.</param>
    /// <param name="said">What was said, already neutralised.</param>
    /// <param name="width">How wide to wrap it.</param>
    /// <returns>The dialog and its session.</returns>
    internal static (Saying View, SessionToken? Token) Open(IApplication app, string said, int width)
    {
        ArgumentNullException.ThrowIfNull(app);

        var view = new Saying(said, width);

        return (view, Prompting.Open(app, view));
    }
}
