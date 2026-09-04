using System.Collections.ObjectModel;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That the surface can be driven by a test at all: a key goes in, a view takes it, and the
/// screen comes back as text.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the gate the rest of the rewrite stands on, and it is a test rather than a note
/// because the answer is a fact about a package.</b> Terminal.Gui ships no headless driver and no
/// supported way to supply one (<see cref="Driving.Session"/> records what was tried and why each
/// route is closed), so what makes the surface testable is a recipe rather than an API: the ANSI
/// driver initialised and never pumped, a screen size set rather than queried, and a session begun
/// rather than run.
/// </para>
/// <para>
/// Every assumption in that recipe is asserted here, so that a package upgrade that breaks one
/// fails on this file with an obvious cause rather than on twenty screen tests with an obscure
/// one.
/// </para>
/// </remarks>
public sealed class HarnessTests
{
    [Fact]
    public void A_view_is_drawn_and_read_back_as_text()
    {
        using var driving = Driving.Open(width: 100, height: 30);

        var window = new Window { Title = "Projects", Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(new Label { Text = "orders answering", Y = 0 });

        driving.Show(window);

        var screen = driving.Text();

        Assert.Contains("Projects", screen, StringComparison.Ordinal);
        Assert.Contains("orders answering", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_reaches_the_view_that_has_focus()
    {
        using var driving = Driving.Open();

        var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        var field = new TextField { Y = 0, Width = 20 };

        window.Add(field);
        driving.Show(window);
        field.SetFocus();

        driving.Type("orders");

        Assert.Equal("orders", field.Text);
    }

    [Fact]
    public void A_click_reaches_the_view_under_it()
    {
        using var driving = Driving.Open();

        var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        var clicked = false;
        var button = new Button { Title = "Take a seat", X = 2, Y = 2 };

        button.Accepting += (_, _) => clicked = true;
        window.Add(button);
        driving.Show(window);

        driving.Click(button);

        Assert.True(clicked, "the button under the pointer did not take the click");
    }

    [Fact]
    public void A_view_is_found_by_its_title_rather_than_by_where_it_is()
    {
        // contracts/surface.md: a region that moves when the layout stacks must not move a test.
        using var driving = Driving.Open(width: 100, height: 30);

        var window = new Window { Title = "Shell", Width = Dim.Fill(), Height = Dim.Fill() };
        var agents = new FrameView { Title = "Agents", X = 40, Width = 30, Height = 10 };

        window.Add(new FrameView { Title = "Projects", Width = 40, Height = 10 }, agents);
        driving.Show(window);

        var before = driving.Find("Agents");

        driving.Resize(40, 30);
        agents.X = 0;
        agents.Y = 10;
        driving.App.LayoutAndDraw(true);

        Assert.Same(before, driving.Find("Agents"));
    }

    [Fact]
    public void A_row_is_chosen_by_its_text_rather_than_by_counted_keys()
    {
        using var driving = Driving.Open();

        var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        var list = new ListView { Width = 30, Height = 5 };

        list.SetSource(new ObservableCollection<string> { "orders", "billing", "inventory" });
        window.Add(list);
        driving.Show(window);

        driving.Row(list, "inventory");

        Assert.Equal(2, list.SelectedItem);
    }

    [Fact]
    public void The_screen_carries_no_attributes()
    {
        // SC-006 rests on this: what a test reads is what somebody with no colour would see.
        using var driving = Driving.Open();

        var window = new Window { Title = "Projects", Width = Dim.Fill(), Height = Dim.Fill() };

        window.Add(new Label { Text = "not answering", Y = 0 });
        driving.Show(window);

        Assert.DoesNotContain("\u001b", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void Time_is_the_test_s_to_move()
    {
        // The seat's idle rule waits twenty minutes and the log tail ticks on an interval. Both
        // are driven by advancing this rather than by sleeping, which is what keeps the suite
        // fast and what keeps it deterministic under load.
        using var driving = Driving.Open();

        var before = driving.Time.Now;

        driving.Time.Advance(TimeSpan.FromMinutes(20));

        Assert.True(driving.Time.Now - before >= TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Driving_a_surface_writes_nothing_to_the_terminal_and_reads_nothing_from_it()
    {
        // The claim the whole recipe rests on, asserted rather than trusted: the main loop is
        // never pumped, and every console read and terminal write lives in it. A package upgrade
        // that started drawing from Init would corrupt the terminal of anybody running the test
        // host directly, which is how this repository runs its tests.
        var stdout = new StringWriter();
        var stdin = new StringReader("HELLO");
        var realOut = Console.Out;
        var realIn = Console.In;

        Console.SetOut(stdout);
        Console.SetIn(stdin);

        try
        {
            using (var driving = Driving.Open())
            {
                var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
                var field = new TextField { Width = 20 };

                window.Add(field);
                driving.Show(window);
                field.SetFocus();
                driving.Press(Key.A);
            }
        }
        finally
        {
            Console.SetOut(realOut);
            Console.SetIn(realIn);
        }

        Assert.Equal(0, stdout.GetStringBuilder().Length);
        Assert.Equal("HELLO", stdin.ReadToEnd());
    }
}
