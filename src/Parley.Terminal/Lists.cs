using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// The one shape of list this surface shows things in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keystroke navigation is off, and that is the whole reason this exists.</b> A
/// <see cref="ListView"/> searches its own items as you type, and it does that by consuming every
/// printable key before the window above it sees one. This surface's actions are keys the status
/// line advertises, so with the navigator on, whichever list had focus would swallow them and
/// nothing on screen would say so.
/// </para>
/// <para>
/// A key is tested by pressing it, not only by checking that no other action steals it: a key tested
/// only in the negative is a key nobody has pressed.
/// </para>
/// <para>
/// Type-to-find is a real loss and it is the right one to take. It is undiscoverable — nothing
/// announces it — while the action keys are written across the status line as a promise, and a
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
/// <b>The race is real.</b> A pool thread alternating a large registry with a one-row one, against
/// repeated draws, throws <see cref="ArgumentOutOfRangeException"/> out of <c>ListWrapper.Render</c>.
/// </para>
/// <para>
/// <b>Why a lock rather than marshalling onto the loop.</b>
/// <see cref="global::Terminal.Gui.App.IApplication.Invoke(System.Action)"/> runs inline on the main
/// thread and queues otherwise, and the harness deliberately never pumps the main loop — a queued
/// update would never run, and <c>Shell.Settled</c> would stop meaning "the screen is true". A lock
/// needs neither, and it puts the guarantee where the hazard is rather than in every caller.
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
    /// Private, because <see cref="Replace"/> is the only way anything needs to take it: a region
    /// swaps the fields a row's colour is derived from through that method's <c>also</c>, in the
    /// same breath as the rows themselves. Handing the lock out instead would let a caller take it
    /// for one of the two and not the other, which is the same race one level up.
    /// </remarks>
    private readonly Lock _guard = new();

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>The cursor is moved off a heading after the toolkit has moved it, not instead.</b> Up,
    /// Down, Home, End and the page keys belong to the toolkit, and a version that reimplemented
    /// them would be a second definition to keep in step — so this lets the list move wherever it
    /// was going to and then carries on the same way until the cursor is on something real.
    /// </para>
    /// <para>
    /// <b>The direction comes from the move itself.</b> Settling always downwards would push Up
    /// straight back off a heading, and the surface would look frozen. A key hook cannot supply the
    /// direction: <see cref="View.OnKeyDown"/> is a <i>pre-process</i> hook, raised before the cursor
    /// has moved. Neither <c>NewKeyDownEvent</c> nor <c>ListView.MoveUp</c> is virtual, so there is no
    /// seam around the move.
    /// </para>
    /// <para>
    /// <see cref="ValueChangedEventArgs{T}"/> carries both ends of it, which makes the question
    /// answerable here and nowhere else: whichever way the cursor just travelled is the way to
    /// carry on. That covers a key, a click and a direct assignment with one rule rather than
    /// three, and it needs no list of which keys mean "up" — a list that would have to be kept in
    /// step with a toolkit that owns the bindings.
    /// </para>
    /// </remarks>
    protected override void OnValueChanged(ValueChangedEventArgs<int?> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        base.OnValueChanged(args);

        // Downwards unless the cursor demonstrably went up. A first move has no old value to
        // compare against, and a heading always has rows under it — otherwise it would not have
        // been drawn — so down is the safe reading of "no idea".
        Settle(args is { OldValue: { } was, NewValue: { } now } && now < was ? -1 : 1);
    }

    /// <summary>Replaces everything the list shows, and what is highlighted in it.</summary>
    /// <param name="rows">The lines to show.</param>
    /// <param name="selected">Which row to highlight, or null for none.</param>
    /// <param name="also">Anything the rows' appearance is derived from, swapped with them.</param>
    internal void Replace(IReadOnlyList<string> rows, int? selected, Action? also = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        lock (_guard)
        {
            also?.Invoke();

            SetSource(new ObservableCollection<string>([.. rows]));

            SelectedItem = selected;
        }
    }

    /// <summary>Which rows the cursor may not rest on, or null when it may rest on any.</summary>
    /// <remarks>
    /// <para>
    /// Set by a region that draws headings among its rows. A heading is not a thing to act on, so
    /// leaving the cursor free to sit on one gives a person a position where every action has
    /// quietly gone away — which reads as the surface having broken rather than as the cursor
    /// being somewhere that means nothing.
    /// </para>
    /// <para>
    /// A predicate rather than a set of indices, because the rows are replaced whole on every
    /// refresh and a set would be a second thing to swap in step with them.
    /// </para>
    /// </remarks>
    internal Func<int, bool>? Skip
    {
        get;

        set
        {
            field = value;

            if (value is not null && !_settling)
            {
                // Whatever is highlighted now may be a heading: the predicate is set after the
                // list exists, and rows arrive later still.
                Settle(1);
            }
        }
    }

    /// <summary>Whether a settle is already running, so the event it raises does not start another.</summary>
    private bool _settling;

    /// <summary>Moves the cursor off a skipped row, in the direction it was already going.</summary>
    /// <param name="direction">-1 towards the top, 1 towards the bottom.</param>
    /// <remarks>
    /// It turns back at the ends rather than stopping on a heading. A list whose first line is a
    /// heading — which is every grouped list — would otherwise leave the cursor on it after Home,
    /// and there is always something below a heading, or the heading would not be there.
    /// </remarks>
    private void Settle(int direction)
    {
        if (_settling)
        {
            return;
        }

        var count = Source?.Count ?? 0;

        if (count == 0 || Skip is not { } skip)
        {
            return;
        }

        var at = SelectedItem ?? 0;

        while (at >= 0 && at < count && skip(at))
        {
            at += direction;
        }

        if (at < 0 || at >= count)
        {
            // Off the end, so come back the other way. The row this lands on is the nearest one
            // that is not a heading, which is where the cursor was trying to get to.
            at = Math.Clamp(SelectedItem ?? 0, 0, count - 1);

            while (at >= 0 && at < count && skip(at))
            {
                at -= direction;
            }
        }

        if (at < 0 || at >= count)
        {
            return;
        }

        // Guarded, because assigning this raises SelectedItemChanged, which settles again. One
        // move is what is wanted; a second would be a loop between two headings.
        _settling = true;

        try
        {
            SelectedItem = at;
        }
        finally
        {
            _settling = false;
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
        lock (_guard)
        {
            return base.OnDrawingContent(context);
        }
    }
}
