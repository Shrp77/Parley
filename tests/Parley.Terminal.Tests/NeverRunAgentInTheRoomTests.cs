using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That an agent that has never run can be chosen in the room, by the keys a person presses.
/// </summary>
/// <remarks>
/// The owner's report of 2026-10-09: the row showed a box and could not be selected. Pressed rather than called, because
/// Space was offered by the surface only on a participant, so on this row the key did nothing and was named nowhere.
/// </remarks>
public sealed class NeverRunAgentInTheRoomTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Seated Seated, List<string> Asked)> OpenAsync()
    {
        var seated = await Seated.OpenAsync(Ct);
        var shell = seated.Shell;
        var asked = new List<string>();

        shell.Room.StartRequested += (_, role) => asked.Add(role);
        shell.Room.Unstarted = ["zeta"];
        shell.Room.Address(shell.Drafting!.Recipients);
        seated.Focus<RoomRegion>();
        seated.Session.Row(shell.Room.List, "zeta");

        return (seated, asked);
    }

    [Fact]
    public async Task Space_on_the_row_asks_to_start_it_and_the_surface_names_the_key()
    {
        var (seated, asked) = await OpenAsync();

        await using (seated)
        {
            Assert.Contains(
                seated.Shell.Actions(),
                action => action.Label == Shell.StartFromRoomTitle && action.Chord is { } chord && chord == Key.Space);

            seated.Session.Press(Key.Space);

            Assert.Equal(["zeta"], asked);
        }
    }

    [Fact]
    public async Task Enter_on_the_row_asks_to_start_it()
    {
        var (seated, asked) = await OpenAsync();

        await using (seated)
        {
            seated.Session.Enter();

            Assert.Equal(["zeta"], asked);
        }
    }
}
