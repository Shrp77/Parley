using Parley.Client.Seating;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// The one line a person speaks from, and what it says Enter will do.
/// </summary>
/// <remarks>
/// <para>
/// <b>One box, and the protocol has four acts.</b> Which one a line becomes is derived per recipient from who
/// owes whom (<see cref="Pairing"/>) rather than chosen before the words: a person knows what they want to say,
/// and Request, Clarify and Result are bookkeeping about turns. The preview above the line is what makes that
/// safe — it names every recipient and what each will get, so nothing is recorded that was not shown.
/// </para>
/// <para>
/// <b>And it names the cost.</b> SPEC.md §5.5 makes a plural ask N exchanges, each owing a Result, and until now
/// this surface let somebody find that out in `parley flags` the next day. Counted in exchanges rather than
/// messages because obligations are what a person is deciding about.
/// </para>
/// <para>
/// <b>The seat is read at the send and never captured here.</b> 0.18.9 found three call sites that took the seat
/// when a form opened and went on speaking under a name the book had released; the rule since is that the send
/// asks.
/// </para>
/// </remarks>
internal sealed class LineRegion : FrameView
{
    /// <summary>What this region is called, in the status line and to a test.</summary>
    internal const string RegionTitle = "Say something";

    /// <summary>What the preview says where there is nobody to send to.</summary>
    internal const string Nobody = "nobody is in this conversation yet";

    /// <summary>What the preview says before a seat has been taken.</summary>
    internal const string NoSeat = "take a seat to say something here";

    /// <summary>What the preview says with an empty line.</summary>
    internal const string Empty = "type to say something";

    /// <summary>The key that takes the other side of the one choice a draft offers.</summary>
    internal const char Other = '\t';

    private readonly TextField _field;
    private readonly Plain _preview;

    internal LineRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        SetScheme(Colours.Region(Colours.Conversations));

        _preview = new Plain
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Fill(1),
            Text = Empty,
        };

        // The line sits against the bottom so the preview above it grows upward as recipients are added,
        // rather than pushing the thing somebody is typing off its own region.
        _field = new TextField
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(2),
        };

        Add(_preview);
        Add(_field);
    }

    /// <summary>What is written but not sent. Named to avoid hiding the toolkit's own Text.</summary>
    /// <remarks>
    /// Read from the field rather than held beside it, so there is one copy of what somebody typed. Two would
    /// eventually disagree, and the one that is wrong would be the one that got sent.
    /// </remarks>
    internal string Written
    {
        get => _field.Text;
        set => _field.Text = value;
    }

    /// <summary>What the preview is saying, for a test to read without the screen.</summary>
    internal string Preview => _preview.Text;

    /// <summary>Puts the plan for the current draft above the line.</summary>
    /// <param name="draft">The draft, or null with no seat.</param>
    /// <remarks>
    /// Every recipient is named rather than counted, because the case worth seeing is one line doing three
    /// different things — which a count would hide.
    /// </remarks>
    internal void Show(Draft? draft)
    {
        if (draft is null)
        {
            _preview.Text = NoSeat;

            return;
        }

        if (draft.Recipients.Count == 0)
        {
            _preview.Text = Nobody;

            return;
        }

        var plan = draft.Plan();
        var cost = draft.Costs();

        var lines = new List<string>
        {
            "to: " + Safe.Text(string.Join(", ", plan.Select(a => a.Peer.Value))),
        };

        foreach (var addressed in plan)
        {
            lines.Add("  " + Safe.Text(addressed.Peer.Value) + "  " + Says(addressed));
        }

        lines.Add(Counted(cost));

        _preview.Text = Reports.Sentences(lines);
    }

    /// <summary>What one recipient will get, and how to take the other side where there is one.</summary>
    /// <param name="addressed">The recipient and its act.</param>
    /// <returns>The clause.</returns>
    /// <remarks>
    /// The alternative is named only where it applies. An action advertised where it would do nothing is the
    /// same fault as a key named on a status line and bound to nothing.
    /// </remarks>
    private static string Says(Addressed addressed) => addressed.Act switch
    {
        Act.Answer => "answer — closes the exchange (Tab: ask back instead)",
        Act.AskBack => "asks them back — the turn goes to them (Tab: answer instead)",
        Act.Clarify => "clarifies your open request",
        _ => "new request — they will owe a result",
    };

    /// <summary>The cost of a send, in the units the protocol counts.</summary>
    /// <param name="cost">What the draft would do.</param>
    /// <returns>The sentence.</returns>
    private static string Counted(Cost cost)
    {
        var parts = new List<string>();

        if (cost.Opens > 0)
        {
            parts.Add($"{cost.Opens} exchange{(cost.Opens == 1 ? string.Empty : "s")} opened, each owing a result");
        }

        if (cost.Closes > 0)
        {
            parts.Add($"{cost.Closes} closed");
        }

        if (cost.HandsBack > 0)
        {
            parts.Add($"{cost.HandsBack} handed back");
        }

        return parts.Count == 0 ? Empty : string.Join(" · ", parts);
    }
}
