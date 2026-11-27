using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>Every dialog the terminal opens: one whose keys are its command table's and nobody else's.</summary>
/// <remarks>
/// <para>
/// <b>The key handler is sealed here and asks only the table.</b> A dialog has no key handler of its own to write, so
/// it has nowhere to compare a key: three rounds of review each found a dialog or the surface taking a key the table had
/// never seen, through a helper the test that scanned the handlers did not read. <c>KeysTakenBeforeTheTableTests</c>
/// fails, by reflection, on any key handler in this assembly other than this one and the surface's.
/// </para>
/// <para>
/// <b>Not generic, so <see cref="Prompting.Open"/> can take it</b>: what can be opened is a table dialog, by type, and
/// the guard reads the same types. Only <see cref="TableDialog{TSelf}"/> derives from it, which is what the private
/// protected constructor and the sealed <see cref="TableDialog{TSelf}.Take"/> hold.
/// </para>
/// <para>
/// Hot keys are off here for every dialog, because a dialog's title carries somebody else's text and is drawn as it is
/// spelled.
/// </para>
/// </remarks>
internal abstract class TableDialog : Dialog
{
    /// <summary>Sets up what every dialog shares.</summary>
    private protected TableDialog()
    {
        HotKeySpecifier = Plain.NoHotKey;
    }

    /// <summary>Answers a key from the dialog's table.</summary>
    /// <param name="key">What arrived.</param>
    /// <returns>Whether the table took it.</returns>
    /// <remarks>
    /// Also what a view inside the dialog hands its keys to first, through <see cref="KeyRouting.TableFirst"/>, where the
    /// view would otherwise take a key the dialog's table answers.
    /// </remarks>
    internal abstract bool Take(Key key);

    /// <inheritdoc/>
    protected sealed override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Take(key) || base.OnKeyDown(key);
    }
}

/// <summary>A dialog of one type, with the table that type builds.</summary>
/// <typeparam name="TSelf">The dialog's own type.</typeparam>
/// <remarks>
/// <para>
/// <b>The table is the type's</b> (<see cref="IKeyTable{TSelf}"/>), built when the dialog is, so a clash stops the
/// constructor; the tool built every table before it took the console, so that never happens to a person.
/// </para>
/// <para>
/// A dialog is one place, with no layer: it is a session on top of the surface and takes every key first, so nothing of
/// the surface's is live in it. A table that declared more than one would be refused here.
/// </para>
/// </remarks>
internal abstract class TableDialog<TSelf> : TableDialog
    where TSelf : TableDialog<TSelf>, IKeyTable<TSelf>
{
    private readonly CommandManager<TSelf> _table = TSelf.BuildTable();
    private readonly KeyContext _place;

    /// <summary>Builds the dialog's table and reads its place.</summary>
    /// <exception cref="InvalidOperationException">The table refuses, or declares other than one place.</exception>
    protected TableDialog()
    {
        _place = _table.Places.Count == 1
            ? _table.Places[0].Context
            : throw new InvalidOperationException($"{typeof(TSelf).Name}'s table declares {_table.Places.Count} places; a dialog is one.");
    }

    /// <summary>The dialog's table.</summary>
    internal CommandManager<TSelf> Table => _table;

    /// <inheritdoc/>
    internal sealed override bool Take(Key key) => _table.Take(_place, (TSelf)this, key);
}
