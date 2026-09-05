using Grpc.Net.Client;
using Parley.Cli.Top;
using Parley.Client;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That one region's work going wrong does not take the surface with it, and that the log view is
/// safe to append to from the thread that follows the file.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, probing rather than reading. Both cases below were found by writing
/// the malicious case and watching it be admitted: the first because a comment claimed containment
/// the code did not have, and the second because the follow loop is the one piece of this surface
/// that runs off the main loop and nothing said so.
/// </para>
/// <para>
/// In a browser an uncaught exception is a page nobody likes. In a terminal it is the end of the
/// session and every other project's view with it, which is why this is lens 1's subject rather
/// than a tidiness note.
/// </para>
/// </remarks>
public sealed class ContainmentTests
{
    [Fact]
    public async Task A_read_that_throws_leaves_the_surface_usable_and_says_what_happened()
    {
        // The probe. Everything the shell queues is a broker read contained by
        // BrokerDirectory.ReadAsync, so what could reach the queue is a defect -- and a defect that
        // faults the chain makes every later `await Settled` rethrow somebody else's exception,
        // long after the thing that caused it.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        shell.Failing(new InvalidOperationException("a region went wrong"));

        // It completes rather than faulting, which is what lets the next action be waited on.
        await shell.Settled;

        Assert.Contains("a region went wrong", shell.Said, StringComparison.Ordinal);

        // And the surface still works: the next thing queued runs and is waited on normally.
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.NotNull(shell.Projects);
    }

    [Fact]
    public async Task A_cancelled_read_says_nothing_because_the_surface_is_going_away()
    {
        // The one exception that is genuinely not news. Reporting it would put "the operation was
        // cancelled" on screen every time somebody quit.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        shell.Failing(new OperationCanceledException());

        await shell.Settled;

        Assert.Equal(string.Empty, shell.Said);
    }

    [Fact]
    public async Task The_log_view_survives_being_appended_to_while_it_is_drawn()
    {
        // The probe for the one part of this surface that runs off the main loop. The follower
        // appends from a thread pool thread and the toolkit draws from the loop; a view whose
        // source collection is mutated during a draw throws, or reads a torn list, and either way
        // the failure lands in the draw rather than anywhere near the cause.
        using var driving = Driving.Open();

        var view = new LogView("following nothing");

        driving.Show(view);

        using var stopping = new CancellationTokenSource();

        var appending = Task.Run(
            () =>
            {
                for (var at = 0; at < 500 && !stopping.IsCancellationRequested; at++)
                {
                    view.Append($"line {at}");
                }
            },
            TestContext.Current.CancellationToken);

        for (var draw = 0; draw < 200; draw++)
        {
            driving.App.LayoutAndDraw(true);
        }

        await appending;
        await stopping.CancelAsync();

        driving.App.LayoutAndDraw(true);

        // Everything written is there, in order, once the view has drawn again.
        Assert.Equal(500, view.Lines.Count);
        Assert.Equal("line 0", view.Lines[0]);
        Assert.Equal("line 499", view.Lines[^1]);
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
