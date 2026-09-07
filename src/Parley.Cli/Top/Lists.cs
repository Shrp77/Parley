using System.Collections.ObjectModel;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Cli.Top;

/// <summary>
/// The one shape of list this surface shows things in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keystroke navigation is off, and that is the whole reason this exists.</b> A
/// <see cref="ListView"/> searches its own items as you type, and it does that by consuming every
/// printable key before the window above it sees one. This surface binds printable keys — <c>c</c>
/// creates a project, <c>r</c> removes one, <c>?</c> lists what a region can do — so with the
/// navigator on, every letter the status line advertises was swallowed by whichever list had
/// focus. Nothing on screen said so: the letters were listed, pressing them did nothing at all,
/// and the empty projects region said <i>Press c to create one</i> under a <c>c</c> that could
/// not work.
/// </para>
/// <para>
/// The suite did not catch it because every test of a key checked the negative — that <c>?</c>
/// does <i>not</i> open a menu over a dialog, that no action steals a letter the surface has
/// already spoken for. <see cref="Terminal.Gui.Input.Key.Tab"/> was the one key checked
/// positively, and Tab is the one printable-key-adjacent thing a list does not eat. A key tested
/// only in the negative is a key nobody has pressed.
/// </para>
/// <para>
/// Type-to-find is a real loss and it is the right one to take. It is undiscoverable — nothing
/// announces it — while the letters are written across the status line as a promise, and a
/// surface where the advertised keys work beats one where an unadvertised convenience does.
/// </para>
/// </remarks>
internal static class Lists
{
    /// <summary>A list that fills what it is put in, and leaves the keyboard alone.</summary>
    /// <param name="accent">
    /// The colour of the region it sits in, or null to take whatever it is put inside.
    /// </param>
    /// <returns>The list.</returns>
    /// <remarks>
    /// Null is for a list inside a dialog. A dialog is drawn on a ground of the toolkit's choosing,
    /// and a list that insisted on the terminal's own background would be a rectangle of the wrong
    /// colour inside it.
    /// </remarks>
    internal static GuardedList Filling(Color? accent = null)
    {
        var list = new GuardedList { Width = Dim.Fill(), Height = Dim.Fill(), KeystrokeNavigator = null };

        if (accent is { } colour)
        {
            list.SetScheme(Colours.Rows(colour));
        }

        return list;
    }

    /// <summary>
    /// Colours the rows of a list from what each row says.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <param name="tint">The colour a row earns, or null when it earns none.</param>
    /// <remarks>
    /// <para>
    /// <b>The highlighted row is never tinted, and that is not a detail.</b> A row attribute set
    /// here replaces the one the toolkit would have drawn, highlight included — so tinting the
    /// selected row would take away the only mark saying which row a key is about to act on, in
    /// the two regions where a key retires an agent or destroys a conversation. The toolkit's own
    /// highlight is left to win.
    /// </para>
    /// <para>
    /// The colour is asked for by row number rather than fixed at the time the list is filled,
    /// because a list is refilled on every read and a parallel array of colours would be one more
    /// thing that can fall out of step with the rows it describes.
    /// </para>
    /// </remarks>
    internal static void Tint(ListView list, Func<int, Color?> tint)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(tint);

        list.RowRender += (_, drawing) =>
        {
            if (list.SelectedItem == drawing.Row && list.HasFocus)
            {
                return;
            }

            if (tint(drawing.Row) is { } colour)
            {
                drawing.RowAttribute = Colours.Row(colour);
            }
        };
    }
}

/// <summary>
/// A list that will not be refilled while it is being drawn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every read this surface makes finishes on a pool thread, and the screen is drawn on
/// another.</b> <c>Shell.SelectAsync</c> awaits the broker with <c>ConfigureAwait(false)</c> and
/// then calls a region's <c>Show</c>, which reaches <see cref="ListView.SetSource{T}"/>. Nothing
/// marshalled that: <c>Shell.Chain</c> builds each read with <c>ContinueWith</c> and no scheduler,
/// and there was no <c>Invoke</c> anywhere in this namespace. Meanwhile the main loop can be part
/// way through <see cref="ListView.OnDrawingContent"/>, which reads the source's count and then
/// indexes it — so swapping the source between those two reads throws out of the toolkit, on the
/// draw thread, which in a terminal is the end of the process.
/// </para>
/// <para>
/// <b>Measured, not reasoned.</b> A sized region, a pool thread alternating a forty-row registry
/// with a one-row one, four thousand draws: four hundred and thirty-four
/// <see cref="ArgumentOutOfRangeException"/>s out of <c>ListWrapper.Render</c>. The first version
/// of that probe reported none, because the region had never been given a size and the row handler
/// had not run once — a passing probe that was testing nothing.
/// </para>
/// <para>
/// <b>Why a lock rather than marshalling onto the loop.</b>
/// <see cref="Terminal.Gui.App.IApplication.Invoke(System.Action)"/> is the toolkit's answer and
/// was tried first: it runs inline on the main thread and queues otherwise. It failed thirty-odd
/// tests, because the harness deliberately never pumps the main loop — a queued update never ran,
/// and <c>Shell.Settled</c> stopped meaning "the screen is true". Making that work needs a
/// draining harness and a new promise from <c>Settled</c>; a lock needs neither, and it puts the
/// guarantee where the hazard is rather than in every caller.
/// </para>
/// <para>
/// The critical section is an assignment, never a computation. A region builds its rows outside
/// the lock and takes it only to swap them in, so the draw thread is never waiting on a broker or
/// on the wrapping of a transcript.
/// </para>
/// </remarks>
internal sealed class GuardedList : ListView
{
    /// <summary>
    /// Held while the list is drawn, and while its rows are replaced.
    /// </summary>
    /// <remarks>
    /// Public to the region so that the fields a row's colour is derived from can be swapped in
    /// the same breath as the rows themselves. A tint read under one lock and rows written under
    /// another is the same race one level up: the colours would describe the previous list.
    /// </remarks>
    internal Lock Guard { get; } = new();

    /// <summary>Replaces everything the list shows, and what is highlighted in it.</summary>
    /// <param name="rows">The lines to show.</param>
    /// <param name="selected">Which row to highlight, or null for none.</param>
    /// <param name="also">Anything the rows' appearance is derived from, swapped with them.</param>
    internal void Replace(IReadOnlyList<string> rows, int? selected, Action? also = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        lock (Guard)
        {
            also?.Invoke();

            SetSource(new ObservableCollection<string>([.. rows]));

            SelectedItem = selected;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="ListView.OnRowRender"/> is raised from inside this, so a region's tint callback
    /// runs with the guard already held and needs no lock of its own —
    /// <see cref="System.Threading.Lock"/> is reentrant, and the callback is on the same thread.
    /// </remarks>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        lock (Guard)
        {
            return base.OnDrawingContent(context);
        }
    }
}
