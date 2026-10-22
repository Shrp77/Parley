using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// One project's settings: what each is now, and what it means.
/// </summary>
/// <remarks>
/// <para>
/// <b>A setting needs a place that states it and explains it before anybody changes it</b>, so
/// settings have a view of their own, reached from the Project menu. A switch named with no subject,
/// such as Auto Accept, says nothing about what is being accepted.
/// </para>
/// <para>
/// <b>The meaning is shown for the selected setting, under the list, in full.</b> A list row does not wrap, so an
/// explanation squeezed onto the row would be cut at the pane's edge, at whatever half of the sentence happened to be
/// last. The row says the name and the value; the paragraph below says what they mean.
/// </para>
/// </remarks>
internal sealed class ConfigurationRegion : FrameView
{
    /// <summary>What this region is called, in the status line and to a test.</summary>
    internal const string RegionTitle = "Configuration";

    /// <summary>The action that changes the setting under the cursor.</summary>
    internal const string ChangeTitle = "Change this setting";

    /// <summary>Said while the settings are being read.</summary>
    internal const string Reading = "Reading this project's settings...";

    /// <summary>
    /// Rows kept for the list, which is more than there are settings today.
    /// </summary>
    /// <remarks>
    /// Fixed rather than sized from the rows, because the rows arrive from a read on a pool thread and a layout change
    /// made there races the main loop's own layout. Room for a few more settings before this needs to change.
    /// </remarks>
    private const int ListRows = 4;

    private readonly GuardedList _list = Lists.Filling(Colours.Projects);

    private readonly Plain _meaning;

    private List<Setting> _settings = [];

    internal ConfigurationRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        SetScheme(Colours.Region(Colours.Projects));

        _list.Height = ListRows;

        _meaning = new Plain
        {
            X = 0,
            Y = Pos.Bottom(_list) + 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = Reading,
        };

        _meaning.TextFormatter.WordWrap = true;

        Add(_list, _meaning);

        _list.ValueChanged += (_, _) => Explain();
    }

    /// <summary>The settings as drawn, in order.</summary>
    internal IReadOnlyList<Setting> Settings => _settings;

    /// <summary>The setting under the cursor, or null when none could be read.</summary>
    internal Setting? Selected =>
        _list.SelectedItem is { } at && at >= 0 && at < _settings.Count ? _settings[at] : null;

    /// <summary>The list, so a test can read its rows.</summary>
    internal ListView List => _list;

    /// <summary>What the paragraph under the list says.</summary>
    internal string Meaning => _meaning.Text;

    /// <summary>Draws the settings.</summary>
    /// <param name="settings">Every setting, in the order they are listed.</param>
    internal void Show(IReadOnlyList<Setting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var keep = Selected?.Name;
        var names = settings.Count == 0 ? 0 : settings.Max(setting => setting.Name.Length);
        var rows = settings.Select(setting => $"{setting.Name.PadRight(names)}  {setting.Value}").ToList();
        var at = keep is null ? 0 : Math.Max(0, settings.ToList().FindIndex(setting => setting.Name == keep));

        _list.Replace(rows, settings.Count == 0 ? null : at, also: () => _settings = [.. settings]);

        Explain();
    }

    /// <summary>Says the settings could not be read, rather than showing none.</summary>
    /// <param name="problem">Why, already made safe to draw.</param>
    /// <remarks>
    /// An empty list would read as a project with nothing to set. A broker that did not answer is a different fact, and
    /// the one somebody needs before deciding anything.
    /// </remarks>
    internal void Unreadable(string problem)
    {
        _list.Replace([], null, also: () => _settings = []);
        _meaning.Text = $"This project's settings could not be read. {problem}";
    }

    /// <summary>Puts the selected setting's meaning under the list.</summary>
    private void Explain()
    {
        if (Selected is { } setting)
        {
            _meaning.Text = $"{setting.Meaning}{Environment.NewLine}{Environment.NewLine}Enter changes it.";
        }
    }
}

/// <summary>One setting of a project, as the configuration view shows it.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Value">What it is now, in a word.</param>
/// <param name="Meaning">What it decides, in full sentences.</param>
internal sealed record Setting(string Name, string Value, string Meaning);
