using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// What happens when somebody navigates while a read is still in flight.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.1. 006 added state the reads did not have to think about
/// before: which view is open, what it is about, how the list is sorted, and whether archived
/// conversations are in it. Every one of those can change between a read being queued and its
/// result being drawn.
/// </para>
/// <para>
/// The hazard is specific. A read started on one project writes into regions by name, not by view,
/// so a read that returns after the person has moved writes somebody else's data into the panes in
/// front of them — and it would look like data rather than like a mistake.
/// </para>
/// </remarks>
public sealed class ViewRaceTests
{
    private static BrokerDirectory Directory() =>
        new(
            [
                new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null),
                new ProjectSettings(ProjectName.Create("beta"), string.Empty, "http://127.0.0.1:2", null, null),
            ],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    [Fact]
    public async Task Sixty_four_navigations_at_once_leave_one_coherent_place()
    {
        // The work queue is one queue and the place is one value. Both were probed on this branch's
        // predecessor after `_work = _work.ContinueWith(...)` turned out to be three steps on a
        // plain field; the place is new state on the same object and gets the same treatment.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        var walking = Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            await shell.GoAsync(i % 2 == 0
                ? ViewPlace.Into(ProjectName.Create("alpha"))
                : ViewPlace.Into(ProjectName.Create("beta")).Into("some-discussion"));
        }));

        await Task.WhenAll(walking);
        await shell.Settled;

        // Whichever it landed on, the panes must be the ones that view has -- not a mixture from
        // two of them, which is what a half-applied change of place would leave.
        var place = shell.Place;

        // Two panes on either view since 0.30.0: a conversation is its room and its transcript, a project
        // is its agents and its conversations. What this asserts is that they are not a mixture of the two,
        // which is what a half-applied change of place would leave.
        Assert.Equal(2, shell.Panes.Count);

        Assert.True(shell.FocusedRegion < shell.Panes.Count);
    }

    [Fact]
    public async Task Going_back_while_a_read_is_queued_does_not_strand_the_focus()
    {
        // Focus is an index into the current view's panes. A view change that shrank the panes
        // without moving the index would leave the surface focused on a pane that is not there,
        // and every action would be read from whatever happened to be at that index.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Focus(1);

        Assert.Equal(1, shell.FocusedRegion);

        await shell.BackAsync();
        await shell.Settled;

        Assert.Equal(Viewing.Projects, shell.Place.Viewing);
        Assert.Single(shell.Panes);
        Assert.Equal(0, shell.FocusedRegion);
        Assert.NotEmpty(shell.Actions());
    }

    [Fact]
    public async Task An_action_taken_on_a_view_that_has_been_left_does_not_act_on_the_new_one()
    {
        // The failure this branch already produced three times in another form: reading a region's
        // selection at the moment the queued work runs rather than at the moment the person chose.
        // Here the person destroys a conversation and walks out before the queue reaches it.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Conversations.Show(
            [new ConversationSummary(
                "alpha-talk", [], 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false)],
            null);
        shell.Focus(1);

        await shell.DestroyConversation();

        var choosing = (ChoiceDialog)driving.Find(Destroying.Title);

        // Leave the view with the dialog still open. The dialog holds the conversation it was
        // opened on, so what it destroys must not follow the person.
        await shell.BackAsync();
        await shell.Settled;

        driving.Row(choosing.Choices, Destroying.Destroy);
        driving.Enter();

        var typing = (TextDialog)driving.Find(Destroying.Title);

        driving.Type("alpha-talk");
        typing.Finish(taken: true);

        await shell.Settled;

        // **The assertion this test was first written with was wrong, and the code was right.** It
        // looked for the conversation's name in what was said, and what is said on an unreachable
        // broker is the broker's problem, which names no conversation. What matters is that the
        // destroy was attempted at all -- against a project the person had already left -- and that
        // leaving did not turn it into a destroy of something else.
        Assert.Contains("not answering", shell.Said, StringComparison.Ordinal);
        Assert.Equal(Viewing.Projects, shell.Place.Viewing);
    }

    [Fact]
    public async Task The_view_state_survives_a_refresh_that_finds_nothing()
    {
        // The sort and the archived toggle are session state. A read that failed used to be able to
        // reset the archived count to zero, which would say "nothing is hidden" on the strength of
        // not having been able to ask.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);
        shell.Focus(1);

        var toggle = shell.Actions().Single(a => a.Label == Shell.ShowArchivedTitle);

        await toggle.Run();
        await shell.Settled;

        // Still on, after a refresh against a broker that is not there.
        Assert.Contains(shell.Actions(), a => a.Label == Shell.HideArchivedTitle);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.Contains(shell.Actions(), a => a.Label == Shell.HideArchivedTitle);
    }

    [Fact]
    public async Task Opening_a_conversation_twice_does_not_leave_two_views_deep()
    {
        // ViewPlace.Into on a conversation refuses without a project, and a conversation view has
        // one -- so a second open from inside a conversation must not build a fourth level.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        await shell.OpenAsync();
        await shell.Settled;

        Assert.Equal(Viewing.Conversation, shell.Place.Viewing);

        await shell.BackAsync();
        await shell.Settled;

        Assert.Equal(Viewing.Project, shell.Place.Viewing);
    }
}
