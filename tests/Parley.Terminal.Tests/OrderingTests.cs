using Grpc.Net.Client;
using Parley.Client;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the last thing somebody did is the last thing drawn.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.5. Every keystroke that reads a broker queues work, and brokers
/// answer at whatever speed they answer at. Run at once, the slower read draws last — so holding an
/// arrow key down the projects list would leave the agents region showing whichever project's
/// broker happened to be slowest, which is not the one the highlight is on.
/// </para>
/// <para>
/// The queue exists to make that impossible, and this is the probe that it does: work is chained,
/// so ordering is a property rather than a race that usually goes the right way.
/// </para>
/// </remarks>
public sealed class OrderingTests
{
    [Fact]
    public async Task Queued_work_runs_in_the_order_it_was_asked_for()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var order = new List<int>();

        // Each is slower than the one after it, which is the shape that breaks an unordered
        // implementation: run at once, they would finish backwards.
        for (var at = 0; at < 5; at++)
        {
            var mine = at;

            shell.Queued(async token =>
            {
                await Task.Delay((5 - mine) * 20, token);

                lock (order)
                {
                    order.Add(mine);
                }
            });
        }

        await shell.Settled;

        Assert.Equal([0, 1, 2, 3, 4], order);
    }

    [Fact]
    public async Task Work_queued_after_a_failure_still_runs_in_order()
    {
        // The two properties together. A chain that dropped its tail on a fault would lose every
        // redraw after the first defect, and the surface would freeze while still accepting keys.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var order = new List<string>();

        shell.Queued(_ =>
        {
            order.Add("first");

            return Task.CompletedTask;
        });

        shell.Failing(new InvalidOperationException("in the middle"));

        shell.Queued(_ =>
        {
            order.Add("last");

            return Task.CompletedTask;
        });

        await shell.Settled;

        Assert.Equal(["first", "last"], order);
        Assert.Equal("in the middle", shell.Said);
    }

    [Fact]
    public async Task Settled_is_the_whole_chain_rather_than_the_last_thing_added()
    {
        // What every driven journey in this suite depends on. If awaiting it returned once the
        // newest item finished, a test would assert on a screen the earlier items had not drawn.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var finished = 0;

        for (var at = 0; at < 4; at++)
        {
            shell.Queued(async token =>
            {
                await Task.Delay(10, token);

                Interlocked.Increment(ref finished);
            });
        }

        await shell.Settled;

        Assert.Equal(4, finished);
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
