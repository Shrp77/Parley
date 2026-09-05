using Grpc.Net.Client;
using Parley.Cli.Top;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That a dialog takes the keyboard while it is open, and gives it back when it closes.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, and the probe that had to be written rather than reasoned about.
/// The shell binds a letter per action — <c>a</c> adds an agent, <c>c</c> creates a project,
/// <c>r</c> removes one — and the dialogs those actions open are full of fields somebody types
/// names into. If the shell saw those keys as well, typing a project name would open a dialog per
/// letter, and typing an agent's name to authorise a purge would be worse than that.
/// </para>
/// <para>
/// Nothing in the code says which way it goes: the shell is a window, a dialog is a separate
/// session on top of it, and whether an unhandled key walks from one to the other is the toolkit's
/// business. So it is asserted rather than assumed.
/// </para>
/// </remarks>
public sealed class DialogStackTests
{
    [Fact]
    public async Task An_action_letter_typed_into_a_dialog_stays_in_the_dialog()
    {
        // Every letter below is bound to something on the projects region: a, c, r, g, e, b.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);

        // The projects have to be read before a region has anything selected, and the seat action
        // is offered for the selected project.
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Tab();

        _ = shell.Join();

        var dialog = (TextDialog)driving.Find(AgentsRegion.JoinTitle);

        driving.Type("cabreg");

        Assert.Equal("cabreg", dialog.Typed);

        // And no second dialog was opened by any of those letters. Finding one would throw; the
        // assertion is that the seat dialog is still the one on top.
        Assert.Same(dialog, driving.Find(AgentsRegion.JoinTitle));
    }

    [Fact]
    public async Task The_question_mark_does_not_open_a_menu_over_an_open_dialog()
    {
        // `?` lists what a region can do, and a project description or an agent's name may contain
        // one. A menu opening over a half-typed field would lose what was in it.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);

        // The projects have to be read before a region has anything selected, and the seat action
        // is offered for the selected project.
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Tab();

        _ = shell.Join();

        var dialog = (TextDialog)driving.Find(AgentsRegion.JoinTitle);

        driving.Type("who?");

        Assert.Equal("who?", dialog.Typed);
    }

    [Fact]
    public async Task Tab_inside_a_dialog_does_not_move_the_region_behind_it()
    {
        // Tab is the surface's own key and a dialog's fields are tabbed between. A dialog that let
        // it through would move the region underneath while somebody was filling in a form, and
        // the form would then be writing into a project they were no longer looking at.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);

        // The projects have to be read before a region has anything selected, and the seat action
        // is offered for the selected project.
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Tab();

        var before = shell.FocusedRegion;

        _ = shell.Join();

        driving.Tab();
        driving.Tab();

        Assert.Equal(before, shell.FocusedRegion);
    }

    [Fact]
    public async Task Closing_a_dialog_gives_the_keyboard_back_to_the_region()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);

        // The projects have to be read before a region has anything selected, and the seat action
        // is offered for the selected project.
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Tab();

        var before = shell.FocusedRegion;

        _ = shell.Join();
        driving.Escape();

        driving.Tab();

        Assert.NotEqual(before, shell.FocusedRegion);
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
