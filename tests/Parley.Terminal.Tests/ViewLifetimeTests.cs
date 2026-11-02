using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the things 005 made survive a redraw still survive a change of view.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.6. 005 established that a seat is released when nothing touches
/// it, that disposal is idempotent, and that a faulting read is contained. Every one of those was
/// proved against a surface with one screen. 006 gave it three, and a view change removes regions
/// from the tree and adds others — which is a new way for any of them to stop being true.
/// </para>
/// <para>
/// The specific worry is the seat. It is an ordinary attachment and must not outlive the person, so
/// it is released when nothing touches it; navigation is somebody touching it, and a view change
/// that failed to count as a touch would release the seat of somebody actively reading.
/// </para>
/// </remarks>
[Integration]
public sealed class ViewLifetimeTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    // `Moving_between_views_counts_as_being_here` was here once. It built a shell with no seat book, so the
    // "seat released" sentence it asserted against could never have been said and the test could not fail; that
    // sentence is gone now too. `IdleSeatTests` holds the idle rule with a real seat.

    [Fact]
    public async Task Disposal_is_still_idempotent_from_any_view()
    {
        // 005's property, re-asked on a surface that now has three states to be disposed from.
        // Nothing here should depend on which regions happen to be in the tree.
        foreach (var viewing in new[] { Viewing.Projects, Viewing.Project, Viewing.Conversation })
        {
            using var driving = Driving.Open();
            using var directory = Directory();
            var shell = new Shell(directory);

            driving.Show(shell);
            await Walking.GoAsync(shell, viewing);

            shell.Dispose();
            shell.Dispose();
        }
    }

    [Fact]
    public async Task A_view_change_after_disposal_does_nothing_rather_than_throwing()
    {
        // A queued read can complete after the surface is gone, and 006 gave the queue a second
        // thing to do at the end: change the view. Reaching a disposed tree from there would be an
        // exception with nowhere to go.
        using var driving = Driving.Open();
        using var directory = Directory();
        var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Dispose();

        await shell.GoAsync(ViewPlace.Top);
        await shell.BackAsync();
    }

    [Fact]
    public async Task The_regions_kept_off_screen_are_not_disposed_by_being_taken_off_it()
    {
        // The whole reason the four objects are kept rather than rebuilt: a selection, a scroll
        // position and a test's reference all survive a walk in and out. A region disposed on
        // removal would come back empty and look like a failed read.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        shell.Conversations.Show(
            [new ConversationSummary(
                "alpha-talk", [], 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false)],
            null);

        // Off screen on the conversation view; arriving at a project reads it again, so the check is made before that.
        await Walking.GoAsync(shell, Viewing.Conversation);

        Assert.Equal("alpha-talk", shell.Conversations.Selected?.Id);
    }

    [Fact]
    public async Task A_faulting_read_started_from_one_view_does_not_stop_the_next()
    {
        // 005's containment, re-asked. The queue is one chain, so a link that faulted used to be
        // able to strand every link after it -- and 006 put navigation on that chain.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        shell.Queued(_ => throw new InvalidOperationException("a read that went wrong"));

        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);

        Assert.Equal(Viewing.Project, shell.Place.Viewing);
    }
}
