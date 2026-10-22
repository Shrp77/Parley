using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That making the terminal narrow is what stacks the regions.
/// </summary>
/// <remarks>
/// <para>
/// Reported from a real terminal: dragging the window in did not stack anything. It could not
/// have. <see cref="Shell.Lay"/> was called once, from the constructor, and passed
/// <see cref="Shell.Threshold"/> itself — so the surface was arranged side by side before anybody
/// saw it and nothing ever rearranged it again.
/// </para>
/// <para>
/// <b>The suite had three tests of stacking and all three passed.</b> Every one of them called
/// <c>Lay</c> itself. A test that performs the thing under test cannot discover that nothing else
/// performs it, and the closer the helper sits to the production path the more convincingly it
/// hides the gap — this one was named for the resize it was standing in for. So these drive the
/// session's own resize and never mention <c>Lay</c>.
/// </para>
/// </remarks>
public sealed class StackOnResizeTests
{
    [Fact]
    public async Task Narrowing_the_terminal_stacks_the_regions()
    {
        using var driving = Driving.Open(width: Shell.Threshold + 40, height: 30);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TerminalCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.False(shell.Stacked);

        driving.Resize(Shell.Threshold - 40, 30);

        Assert.True(shell.Stacked);
    }

    [Fact]
    public async Task Widening_it_again_brings_the_four_back_side_by_side()
    {
        using var driving = Driving.Open(width: Shell.Threshold - 40, height: 30);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TerminalCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.True(shell.Stacked);

        driving.Resize(Shell.Threshold + 40, 30);

        Assert.False(shell.Stacked);
    }

    [Fact]
    public async Task A_surface_opened_narrow_is_stacked_before_anybody_sees_it()
    {
        // The constructor cannot know the width — it runs before the session has a screen. So the
        // first arrangement is whatever the first layout says, and opening `parley terminal` in a
        // narrow terminal must not need a drag to become readable.
        using var driving = Driving.Open(width: Shell.Threshold - 40, height: 30);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TerminalCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.True(shell.Stacked);
        Assert.Contains(ProjectsRegion.RegionTitle, driving.Text(), StringComparison.Ordinal);
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
