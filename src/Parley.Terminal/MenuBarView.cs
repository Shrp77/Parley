using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using ToolkitItem = Terminal.Gui.Views.MenuItem;

namespace Parley.Terminal;

/// <summary>
/// The menu bar on the top row: the menus' titles, each dropping a menu down beneath it.
/// </summary>
/// <param name="menus">What the bar offers now, read at the moment a menu is opened.</param>
/// <param name="run">
/// Runs the action a menu line names, found again at the moment it is chosen. The line carries its key and
/// label rather than the action itself, so a menu can never act on something other than what the screen
/// offers when the choice is made — acting on a different agent than the one under the cursor is the
/// mistake this surface cannot take back.
/// </param>
/// <remarks>
/// <para>
/// <b>A dropdown rather than a dialog per menu.</b> What drops down is the toolkit's own
/// <see cref="PopoverMenu"/>, with every line's hotkeys switched off as <see cref="Plain"/> does, so an
/// underscore in a label reaches the screen.
/// </para>
/// <para>
/// <b>The row is drawn here rather than being the toolkit's <see cref="MenuBar"/>.</b> The toolkit's titles
/// are auto-sized views living in the shell's tree, and the shell rearranges itself from whichever thread a
/// broker's reply finished on, which made <c>RefillRaceTests</c> fail intermittently. This row has no
/// subviews at all, so nothing in the shell's tree changes when the menus do; a dropdown is a popover,
/// which the toolkit keeps outside that tree, and it is built on the loop when somebody opens it.
/// </para>
/// <para>
/// <b>What is on it is still <see cref="MenuRow"/>'s reading of the actions and nothing else.</b> This type
/// draws that reading; it decides nothing about what a menu holds.
/// </para>
/// </remarks>
internal sealed class MenuBarView(
    Func<IReadOnlyList<(string Title, IReadOnlyList<MenuItem> Items)>> menus,
    Action<string, string> run) : View
{
    /// <summary>The columns before the first title and between each pair.</summary>
    private const int Gap = 2;

    /// <summary>What the bar says after its titles, naming the key that opens it.</summary>
    internal const string MenusHint = "(F10)";

    private readonly Lock _gate = new();
    private IReadOnlyList<string> _titles = [];
    private PopoverMenu? _open;
    private string? _openTitle;

    /// <summary>The title of the menu dropped down now, or <see langword="null"/> when none is.</summary>
    internal string? Opened => _open is { Visible: true } ? _openTitle : null;

    /// <summary>Records the titles to draw.</summary>
    /// <param name="titles">The menus' titles, left to right.</param>
    /// <remarks>
    /// Safe from any thread: it swaps one immutable list and asks for a redraw, as the status line beside it does.
    /// </remarks>
    internal void Show(IReadOnlyList<string> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);

        lock (_gate)
        {
            if (_titles.SequenceEqual(titles, StringComparer.Ordinal))
            {
                return;
            }

            _titles = [.. titles];
        }

        SetNeedsDraw();
    }

    /// <summary>Drops down the menu whose title starts with a letter.</summary>
    /// <param name="initial">The first letter of the title clicked.</param>
    /// <returns><see langword="true"/> when a menu opened.</returns>
    /// <remarks>
    /// The menus are read now rather than remembered, so what drops down is what the focused region offers
    /// at this moment — a menu kept from the last redraw could be the previous region's.
    /// </remarks>
    internal bool Open(char initial)
    {
        var offered = menus();

        for (var i = 0; i < offered.Count; i++)
        {
            if (char.ToUpperInvariant(offered[i].Title[0]) == char.ToUpperInvariant(initial))
            {
                return OpenAt(offered, i);
            }
        }

        return false;
    }

    /// <summary>Drops down the first menu, which is what F10 does.</summary>
    /// <returns><see langword="true"/> when a menu opened.</returns>
    /// <remarks>
    /// <b>F10 rather than Alt and a letter</b>: Alt and a letter is every action's key, so that a plain letter is
    /// never an action and typing can never set one off. F10 is the menu key on most programs that have a bar, and
    /// Left and Right walk from the first menu to the rest.
    /// </remarks>
    internal bool OpenFirst()
    {
        var offered = menus();

        return offered.Count > 0 && OpenAt(offered, 0);
    }

    /// <summary>Drops down one menu, taking down whichever was open.</summary>
    /// <param name="offered">The menus as read now.</param>
    /// <param name="at">Which one.</param>
    /// <returns><see langword="true"/> when it opened.</returns>
    private bool OpenAt(IReadOnlyList<(string Title, IReadOnlyList<MenuItem> Items)> offered, int at)
    {
        if (App?.Popovers is not { } popovers)
        {
            return false;
        }

        Show([.. offered.Select(menu => menu.Title)]);
        Close(popovers);

        var (title, items) = offered[at];
        var menu = new PopoverMenu(Views(items));

        // **Left and Right go to the neighbouring menu**, because F10 opens only the first and a bar a person can enter
        // and not cross is half a bar. Taken on the menu's own key event, which runs before its bindings do.
        menu.KeyDown += (_, key) =>
        {
            if (key == Key.CursorRight || key == Key.CursorLeft)
            {
                key.Handled = true;

                var next = (at + (key == Key.CursorRight ? 1 : -1) + offered.Count) % offered.Count;
                var now = menus();

                if (now.Count > 0)
                {
                    OpenAt(now, Math.Min(next, now.Count - 1));
                }
            }
        };

        popovers.Register(menu);

        var frame = FrameToScreen();

        _open = menu;
        _openTitle = title;
        menu.MakeVisible(new Point(frame.X + Left(offered.Select(each => each.Title).ToList(), at), frame.Y + 1), null);

        return true;
    }

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        IReadOnlyList<string> titles;

        lock (_gate)
        {
            titles = _titles;
        }

        var normal = GetAttributeForRole(VisualRole.Normal);

        SetAttribute(normal);
        Move(0, 0);
        AddStr(new string(' ', Math.Max(Viewport.Width, 0)));

        // No letter is underlined: Alt and a letter is an action's key, and an underline would advertise a key that
        // does something else. F10 is named on the row itself, after the titles.
        for (var i = 0; i < titles.Count; i++)
        {
            Move(Left(titles, i), 0);
            AddStr(titles[i]);
        }

        if (titles.Count > 0)
        {
            Move(Left(titles, titles.Count), 0);
            AddStr(MenusHint);
        }

        return true;
    }

    /// <inheritdoc/>
    protected override bool OnMouseEvent(Mouse mouse)
    {
        ArgumentNullException.ThrowIfNull(mouse);

        if (!mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked) || mouse.Position is not { } where)
        {
            return base.OnMouseEvent(mouse);
        }

        IReadOnlyList<string> titles;

        lock (_gate)
        {
            titles = _titles;
        }

        for (var i = 0; i < titles.Count; i++)
        {
            var left = Left(titles, i);

            if (where.X >= left && where.X < left + titles[i].Length)
            {
                return Open(titles[i][0]);
            }
        }

        return base.OnMouseEvent(mouse);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && App?.Popovers is { } popovers)
        {
            Close(popovers);
        }

        base.Dispose(disposing);
    }

    /// <summary>The column a title starts at.</summary>
    /// <param name="titles">Every title on the bar.</param>
    /// <param name="index">Which one.</param>
    /// <returns>The column.</returns>
    /// <remarks>One calculation for drawing, clicking and placing the dropdown, so the three cannot drift.</remarks>
    private static int Left(IReadOnlyList<string> titles, int index)
    {
        var left = Gap;

        for (var i = 0; i < index; i++)
        {
            left += titles[i].Length + Gap;
        }

        return left;
    }

    /// <summary>Takes down the menu opened last, and lets go of it.</summary>
    /// <param name="popovers">Where it was registered.</param>
    /// <remarks>
    /// Every open registers a new menu, so every open must deregister the old one or the toolkit keeps a
    /// menu per keystroke for the life of the program.
    /// </remarks>
    private void Close(ApplicationPopover popovers)
    {
        if (_open is not { } open)
        {
            return;
        }

        if (open.Visible)
        {
            popovers.Hide(open);
        }

        popovers.DeRegister(open);
        open.Dispose();

        _open = null;
        _openTitle = null;
    }

    /// <summary>One menu's lines as toolkit views: an item per action, a rule per separator.</summary>
    /// <param name="items">The menu's lines.</param>
    /// <returns>The views.</returns>
    private IEnumerable<View> Views(IReadOnlyList<MenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.Divides)
            {
                yield return new Line();

                continue;
            }

            var (stroke, label) = (item.Stroke, item.Label);

            // The key goes in the help column rather than being bound. A bound key would be answered by a
            // menu nobody has opened -- the toolkit offers keys to registered popovers -- and the region's
            // own handler answers it too, so one press would run the action twice.
            var line = new ToolkitItem(label, stroke, () => run(stroke, label));

            if (line.CommandView is { } command)
            {
                command.HotKeySpecifier = Plain.NoHotKey;
                command.Text = label;
            }

            yield return line;
        }
    }
}
