using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That the key the status line offers actually opens the menu it names.
/// </summary>
/// <remarks>
/// <para>
/// Reported from a real terminal: <c>?</c> did nothing on the projects region. The suite had a
/// test that <c>?</c> does <i>not</i> open a menu over a dialog, and one that no action steals the
/// letter — but none that it opens one when it should. A key checked only in the negative is a key
/// nobody has pressed.
/// </para>
/// <para>
/// The letters are here for the same reason. They travel the identical route, so if the route is
/// what is broken they are broken too, and the report would have named only the key its author
/// happened to try.
/// </para>
/// </remarks>
public sealed class ActionsKeyTests
{
    [Fact]
    public async Task The_actions_key_opens_the_menu_on_the_projects_region()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        shell.Focus(0);
        driving.Press(new Key(Shell.ActionsKey));

        Assert.NotNull(driving.Find(Shell.ActionsTitle));
    }

    [Fact]
    public async Task The_actions_key_opens_the_menu_with_shift_carried_on_it()
    {
        // A terminal reaches `?` through Shift, and a driver is free to say so on the key. If the
        // surface compares against a bare `?` then the same keystroke matches under one driver and
        // not another, which is exactly the shape of a bug that only appears outside the suite.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        shell.Focus(0);
        driving.Press(new Key(Shell.ActionsKey).WithShift);

        Assert.NotNull(driving.Find(Shell.ActionsTitle));
    }

    [Fact]
    public async Task A_letter_bound_to_an_action_takes_that_action()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        shell.Focus(0);

        var create = shell.Actions().Single(action => action.Label == Naming.CreateTitle);

        driving.Press(new Key(create.Key));

        Assert.NotNull(driving.Find(Naming.CreateTitle));
    }

    [Fact]
    public async Task A_control_chord_is_not_the_letter_it_carries()
    {
        // Ctrl+C is how half the world stops a program. An action that answered to it as readily
        // as to `c` would open a dialog on the chord somebody pressed to leave.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        shell.Focus(0);

        var create = shell.Actions().Single(action => action.Label == Naming.CreateTitle);

        driving.Press(new Key(create.Key).WithCtrl);
        driving.Press(new Key(create.Key).WithAlt);

        Assert.False(driving.Has(Naming.CreateTitle));
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
