using System.Drawing;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;
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
/// <b>A dropdown rather than a dialog per menu, which reverses 021.</b> 021 opened each menu as a
/// <c>Prompting.Choose</c> dialog in the middle of the screen. The owner's call on 2026-09-27, after using
/// it: a menu should be a menu. What drops down is the toolkit's own <see cref="PopoverMenu"/>, with every
/// line's hotkeys switched off as <see cref="Plain"/> does, so an underscore in a label reaches the screen.
/// </para>
/// <para>
/// <b>The row is drawn here rather than being the toolkit's <see cref="MenuBar"/>, and that was measured,
/// not preferred.</b> The toolkit's bar was built first. Its titles are auto-sized views living in the
/// shell's tree, and the shell rearranges itself from whichever thread a broker's reply finished on — so
/// <c>RefillRaceTests</c> failed one run in three, once with the toolkit walking views the bar was
/// replacing and once with a layout pass finding an auto-sized title half computed. Clean <c>HEAD</c>
/// passed the same test ten runs in ten. This row has no subviews at all, so nothing in the shell's tree
/// changes when the menus do; a dropdown is a popover, which the toolkit keeps outside that tree, and it is
/// built on the loop when somebody opens it.
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

    private readonly Lock _gate = new();
    private IReadOnlyList<string> _titles = [];
    private PopoverMenu? _open;
    private string? _openTitle;

    /// <summary>The title of the menu dropped down now, or <see langword="null"/> when none is.</summary>
    internal string? Opened => _open is { Visible: true } ? _openTitle : null;

    /// <summary>Records the titles to draw.</summary>
    /// <param name="titles">The menus' titles, left to right.</param>
    /// <remarks>
    /// Safe from any thread: it swaps one immutable list and asks for a redraw, which is what the status
    /// line beside it has always done.
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
    /// <param name="initial">The letter pressed with Alt, or the first letter of the title clicked.</param>
    /// <returns><see langword="true"/> when a menu opened.</returns>
    /// <remarks>
    /// The menus are read now rather than remembered, so what drops down is what the focused region offers
    /// at this moment — a menu kept from the last redraw could be the previous region's, which is D2's
    /// cross-region read reached through the bar.
    /// </remarks>
    internal bool Open(char initial)
    {
        if (App?.Popovers is not { } popovers)
        {
            return false;
        }

        var offered = menus();

        Show([.. offered.Select(menu => menu.Title)]);

        var at = -1;

        for (var i = 0; i < offered.Count; i++)
        {
            if (char.ToUpperInvariant(offered[i].Title[0]) == char.ToUpperInvariant(initial))
            {
                at = i;

                break;
            }
        }

        if (at < 0)
        {
            return false;
        }

        Close(popovers);

        var (title, items) = offered[at];
        var menu = new PopoverMenu(Views(items));

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
        var hot = new Attribute(normal.Foreground, normal.Background, TextStyle.Underline);

        SetAttribute(normal);
        Move(0, 0);
        AddStr(new string(' ', Math.Max(Viewport.Width, 0)));

        for (var i = 0; i < titles.Count; i++)
        {
            var title = titles[i];

            Move(Left(titles, i), 0);

            // The letter Alt opens it with is underlined, as it is on any other program's bar. It is the
            // one mark on this row that says the key exists.
            SetAttribute(hot);
            AddRune(new Rune(title[0]));
            SetAttribute(normal);
            AddStr(title[1..]);
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
