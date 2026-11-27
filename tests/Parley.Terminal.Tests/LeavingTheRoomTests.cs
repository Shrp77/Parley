using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.ViewBase;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That going back out of a conversation takes the room and the line off the screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the owner's screenshot of 0.35.0.</b> They opened a conversation and went back to
/// the project. The Room column and the Say-something box were still drawn over the project view,
/// with the agents pane squeezed between them and its title covered.
/// </para>
/// <para>
/// The arrangement hides every region not in the current view, but its list of regions still named
/// the four from before 017. The room and the line were added to the tree and never to that list.
/// Nothing showed on the first visit because a region nobody has laid out has no size. After a
/// conversation gave them one, nothing ever hid them again.
/// </para>
/// <para>
/// So this asks every region the shell holds, not the ones a list names. A seventh region added the
/// same way fails here rather than on somebody's screen.
/// </para>
/// </remarks>
[Integration]
public sealed class LeavingTheRoomTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    [Theory]
    [InlineData(Viewing.Project)]
    [InlineData(Viewing.Projects)]
    public async Task Only_the_current_views_panes_are_on_screen_after_leaving_a_conversation(Viewing to)
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        Assert.True(shell.Room.Visible, "the room was never on screen, so leaving it proves nothing");
        Assert.True(shell.Line.Visible, "the line was never on screen, so leaving it proves nothing");

        await Walking.GoAsync(shell, to);

        var regions = new View[]
        {
            shell.Projects, shell.Agents, shell.Conversations, shell.Transcript, shell.Room, shell.Line,
            shell.Configuration,
        };

        var stray = regions
            .Where(region => region.Visible && !shell.Panes.Contains(region))
            .Select(region => region.GetType().Name)
            .ToList();

        Assert.True(stray.Count == 0, $"still on screen after going to {to}: {string.Join(", ", stray)}");
    }
}
