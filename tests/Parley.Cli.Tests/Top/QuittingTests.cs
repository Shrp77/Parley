using Grpc.Net.Client;
using Parley.Cli.Top;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That there is a way out of the surface, that it is written where somebody can find it, and
/// that no other key takes it by accident.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because the way out did not exist and a comment said it did.</b>
/// <c>TopCommand</c> justified not intercepting Ctrl+C partly on the grounds that "Ctrl+Q leaves
/// the surface properly". Terminal.Gui 2.4.17 binds ten keys at application scope, all of them
/// navigation, and Ctrl+Q is not among them — so the chord did nothing at all, and the only
/// mention of it anywhere in the suite was a comment on a test that disposes the surface directly.
/// </para>
/// <para>
/// <b>What did quit was Escape.</b> The toolkit binds <c>Esc =&gt; Quit</c> at application scope,
/// and the surface returned false for Escape at the top of the tree — deliberately, on the
/// reasoning that it should be left "to whatever else wants it". What wanted it ended the program.
/// That is the one thing <c>NavigatingTests</c> says by name must not happen, and it happened,
/// because the test named for it asserted on the view rather than on whether a stop had been
/// asked for.
/// </para>
/// <para>
/// So every assertion here is about <see cref="IRunnable.StopRequested"/>, which is the thing
/// neither of them looked at. The harness does not run the main loop, so a session is never
/// actually popped — a test asserting on the session stack would report nothing had happened in
/// both the fixed case and the broken one.
/// </para>
/// </remarks>
public sealed class QuittingTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    [Fact]
    public async Task The_quit_key_asks_the_application_to_stop()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.False(((IRunnable)shell).StopRequested);

        driving.Press(Shell.QuitKey);

        Assert.True(((IRunnable)shell).StopRequested);
    }

    [Fact]
    public async Task It_works_from_every_view_rather_than_only_the_top()
    {
        // A person who wants out wants out from wherever they are, and the view they are most
        // likely to be stuck on is the deepest one.
        foreach (var viewing in new[] { Viewing.Projects, Viewing.Project, Viewing.Conversation })
        {
            using var driving = Driving.Open();
            using var directory = Directory();
            using var shell = new Shell(directory);

            driving.Show(shell);
            await Walking.GoAsync(shell, viewing);

            driving.Press(Shell.QuitKey);

            Assert.True(
                ((IRunnable)shell).StopRequested,
                $"the quit key did nothing on the {viewing} view");
        }
    }

    [Fact]
    public async Task It_is_written_on_the_status_line_of_every_view()
    {
        // The rule the way back was fixed under, applied to the other way out. A key nobody is
        // told about is a key nobody presses, and this one is the difference between leaving and
        // closing the terminal window.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var checked_ = 0;

        await foreach (var _ in Walking.EveryPaneAsync(shell))
        {
            Assert.Contains(Shell.QuitLabel, shell.Status, StringComparison.Ordinal);
            checked_++;
        }

        // Non-vacuity: an empty walk would make this pass by never looking at a status line.
        Assert.True(checked_ > 0, "no pane was visited, so no status line was read");
    }

    [Fact]
    public async Task It_is_in_the_menu_the_pointer_reaches()
    {
        // contracts/surface.md: no action exists only as a key. A chord is the least guessable
        // kind of key there is, so this is the one that most needs the other route.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        await shell.Offer();

        var menu = Assert.IsType<ChoiceDialog>(driving.Find(Shell.ActionsTitle));

        driving.Row(menu.Choices, Shell.QuitLabel);
        driving.Enter();

        Assert.True(((IRunnable)shell).StopRequested);
    }

    [Fact]
    public async Task Escape_does_not_quit_however_many_times_it_is_pressed()
    {
        // **The regression this file exists for.** Escape reaching the top is now by design rather
        // than by accident: the status line tells people to press it to go back, so one press too
        // many is an ordinary mistake. It must cost nothing.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        for (var press = 0; press < 5; press++)
        {
            driving.Press(Key.Esc);
            await shell.Settled;
        }

        Assert.Equal(Viewing.Projects, shell.Place.Viewing);
        Assert.False(
            ((IRunnable)shell).StopRequested,
            "walking out with Escape and pressing it again ended the program");
    }

    [Fact]
    public async Task The_letter_alone_is_not_the_chord()
    {
        // `q` belongs to no action today, so a surface that answered to the bare letter would look
        // correct until somebody bound `q` to one — and would meanwhile quit on a keystroke that a
        // person typing into a list means as a search.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Press(new Key('q'));

        Assert.False(((IRunnable)shell).StopRequested);
    }

    [Fact]
    public async Task A_dialog_takes_the_chord_rather_than_the_surface_under_it()
    {
        // A dialog is a session on top of this one and gets keys first. Somebody who opened a form
        // and reached for the way out closes the form; they do not leave the program from inside a
        // half-filled one.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await shell.Offer();

        Assert.True(driving.Has(Shell.ActionsTitle), "the menu did not open");

        driving.Press(Shell.QuitKey);

        Assert.False(
            ((IRunnable)shell).StopRequested,
            "the chord passed through an open dialog and ended the program");
    }
}
