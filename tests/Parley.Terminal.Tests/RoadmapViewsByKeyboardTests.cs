#pragma warning disable CS0618 // TextView is obsolete; the readers are TextViews, and the walk names which one has the focus.
using Parley.Client.Roadmaps;
using Parley.Core;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the roadmap views are whole by the keyboard alone: every control is reached, and every key chooses once.
/// </summary>
/// <remarks>
/// Kept from review round 18, where it held, after the proposal view's heading stopped taking the focus: Tab reaches
/// both readers and every button but never the heading, and each choice is taken once from every focus.
/// </remarks>
public sealed class RoadmapViewsByKeyboardTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private static RoadmapProposal Proposal() =>
        new(
            1,
            Orders,
            null,
            null,
            new RoadmapContent(
                "Billing export",
                [
                    new RoadmapItemDraft(null, "Add CSV export", Billing, []),
                    new RoadmapItemDraft(null, "Call it from the report", Orders, [1]),
                ]),
            40,
            ProposalState.Waiting,
            T0,
            null);

    private static Roadmap Plan() =>
        new(
            1,
            1,
            "Billing export",
            RoadmapOrigin.Proposed,
            "orders@test",
            1,
            [
                new RoadmapItem(1, 1, 1, "Add CSV export", Billing, [], "rm1-1", null, null),
                new RoadmapItem(2, 2, 2, "Call it from the report", Orders, [1], "rm1-2", null, null),
            ]);

    private static string Describe(View? view) =>
        view switch
        {
            null => "(nothing)",
            Button b => "button:" + b.Title,
            TextView t => "reader:" + t.Text.Split('\n')[0],
            ListView => "list",
            _ => view.GetType().Name + ":" + view.Text,
        };

    private static List<string> Walk(Driving.Session driving, View dialog, Key key, int presses)
    {
        var seen = new List<string> { Describe(dialog.MostFocused) };

        for (var n = 0; n < presses; n++)
        {
            driving.Press(key);
            seen.Add(Describe(dialog.MostFocused));
        }

        return seen;
    }

    [Fact]
    public void Proposal_view_opens_on_the_roadmap_and_tab_reaches_every_control_but_the_heading()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        var view = new ProposalView(Proposal());
        Prompting.Open(driving.App, view);

        var forward = Walk(driving, view, Key.Tab, 10);
        var back = Walk(driving, view, Key.Tab.WithShift, 10);

        var all = forward.Concat(back).ToHashSet();
        var report = string.Join(" | ", forward) + "  <<  " + string.Join(" | ", back);

        Assert.StartsWith("reader:1.", forward[0], StringComparison.Ordinal);
        Assert.Contains("button:" + ProposalView.ConfirmLabel, all);
        Assert.Contains("button:" + ProposalView.EditLabel, all);
        Assert.Contains("button:" + ProposalView.DeclineLabel, all);
        Assert.Contains("button:" + ProposalView.LeaveLabel, all);
        Assert.DoesNotContain(all, s => s.Contains("Proposed by", StringComparison.Ordinal));
        Assert.True(all.Count >= 6, report);
    }

    public static TheoryData<int, string> ProposalKeys()
    {
        var data = new TheoryData<int, string>();

        for (var tabs = 0; tabs < 7; tabs++)
        {
            foreach (var key in new[] { "c", "e", "d", "esc" })
            {
                data.Add(tabs, key);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ProposalKeys))]
    public void Proposal_view_each_key_chooses_once_from_every_focus(int tabs, string which)
    {
        using var driving = Driving.Open(width: 140, height: 40);
        var view = new ProposalView(Proposal());
        var chosen = new List<ProposalChoice>();
        view.Done += (_, c) => chosen.Add(c);
        Prompting.Open(driving.App, view);

        for (var n = 0; n < tabs; n++)
        {
            driving.Tab();
        }

        var at = Describe(view.MostFocused);
        var (key, expected) = which switch
        {
            "c" => (Key.C.WithAlt, ProposalChoice.Confirm),
            "e" => (Key.E.WithAlt, ProposalChoice.Edit),
            "d" => (Key.D.WithAlt, ProposalChoice.Decline),
            _ => (Key.Esc, ProposalChoice.Leave),
        };

        driving.Press(key);

        Assert.True(chosen.SequenceEqual([expected]), $"at {at}: chose [{string.Join(",", chosen)}]");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Proposal_view_enter_chooses_at_most_once_and_only_on_a_button(int tabs)
    {
        using var driving = Driving.Open(width: 140, height: 40);
        var view = new ProposalView(Proposal());
        var chosen = new List<ProposalChoice>();
        view.Done += (_, c) => chosen.Add(c);
        Prompting.Open(driving.App, view);

        for (var n = 0; n < tabs; n++)
        {
            driving.Tab();
        }

        var at = Describe(view.MostFocused);

        driving.Enter();

        Assert.True(chosen.Count <= 1, $"at {at}: chose [{string.Join(",", chosen)}]");

        if (!at.StartsWith("button:", StringComparison.Ordinal))
        {
            Assert.True(chosen.Count == 0, $"at {at}: Enter chose [{string.Join(",", chosen)}]");
        }
    }

    [Fact]
    public void Plan_view_tab_reaches_every_control()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        var standing = ItemStates.Of(Plan(), [], PersonSeat.On(Walking.Alpha));
        var view = new PlanView(Plan(), standing);
        Prompting.Open(driving.App, view);

        var forward = Walk(driving, view, Key.Tab, 10);
        var all = forward.ToHashSet();

        Assert.Equal("list", forward[0]);
        Assert.True(all.Count(s => s.StartsWith("button:", StringComparison.Ordinal)) == 4, string.Join(" | ", forward));
    }

    public static TheoryData<int, string> PlanKeys()
    {
        var data = new TheoryData<int, string>();

        for (var tabs = 0; tabs < 7; tabs++)
        {
            foreach (var key in new[] { "e", "esc", "enter" })
            {
                data.Add(tabs, key);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PlanKeys))]
    public void Plan_view_each_key_acts_once_from_every_focus(int tabs, string which)
    {
        using var driving = Driving.Open(width: 140, height: 40);
        var standing = ItemStates.Of(Plan(), [], PersonSeat.On(Walking.Alpha));
        var view = new PlanView(Plan(), standing);
        var acts = new List<string>();
        view.EditRequested += (_, _) => acts.Add("edit");
        view.Closed += (_, _) => acts.Add("closed");
        view.ItemChosen += (_, item) => acts.Add("item " + item.Number);
        Prompting.Open(driving.App, view);

        for (var n = 0; n < tabs; n++)
        {
            driving.Tab();
        }

        var at = Describe(view.MostFocused);

        switch (which)
        {
            case "e":
                driving.Press(Key.E.WithAlt);
                Assert.True(acts.SequenceEqual(["edit"]), $"at {at}: [{string.Join(",", acts)}]");
                break;
            case "esc":
                driving.Escape();
                Assert.True(acts.SequenceEqual(["closed"]), $"at {at}: [{string.Join(",", acts)}]");
                break;
            default:
                driving.Enter();
                Assert.True(acts.Count <= 1, $"at {at}: [{string.Join(",", acts)}]");
                break;
        }
    }
}
