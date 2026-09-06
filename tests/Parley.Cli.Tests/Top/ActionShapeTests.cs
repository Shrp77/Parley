using Grpc.Net.Client;
using Parley.Cli.Top;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That every action can be reached, and that no two things answer to the same name.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.3. The surface addresses two things by name: an action by its
/// letter, and a dialog by its title. A collision in either is silent — a second action bound to a
/// letter already taken is simply never reached, and two dialogs sharing a title make every test
/// that finds one by title ambiguous, so the suite would be driving whichever the search met first.
/// </para>
/// <para>
/// Both are the kind of thing that goes wrong by addition rather than by mistake, which is why it
/// is a test and not a review note.
/// </para>
/// </remarks>
public sealed class ActionShapeTests
{
    [Fact]
    public async Task No_region_binds_one_letter_to_two_actions()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);

        await foreach (var _ in Walking.EveryPaneAsync(shell))
        {
            var letters = shell.Actions().Select(action => action.Key).ToList();

            Assert.Equal(letters.Count, letters.Distinct().Count());
        }
    }

    [Fact]
    public async Task No_action_takes_a_letter_the_surface_has_already_spoken_for()
    {
        // Tab, Enter, Escape and the actions menu are the surface's own, and an action bound to
        // one of them would be unreachable while looking perfectly reachable in the status line.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);

        await foreach (var _ in Walking.EveryPaneAsync(shell))
        {
            Assert.DoesNotContain(shell.Actions(), action => action.Key == Shell.ActionsKey);
        }
    }

    [Fact]
    public async Task Every_action_says_what_it_is_in_the_words_its_dialog_uses()
    {
        // The status line and the dialog have to agree, or a person reads one label and meets
        // another. Both come from the same constant.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);

        // Project actions live on the projects view now, and agent actions inside a project. That
        // is the whole of 006's rearrangement, asserted rather than assumed.
        await Walking.GoAsync(shell, Viewing.Projects);

        Assert.Contains(shell.Actions(), action => action.Label == Naming.CreateTitle);
        Assert.Contains(shell.Actions(), action => action.Label == Removing.Title);
        Assert.Contains(shell.Actions(), action => action.Label == Onboarding.Title);

        await Walking.GoAsync(shell, Viewing.Project);

        Assert.Contains(shell.Actions(), action => action.Label == Adding.Title);
    }

    [Fact]
    public void No_two_dialogs_answer_to_the_same_title()
    {
        // Every test in this suite finds a dialog by its title. Two sharing one would make those
        // searches ambiguous, and the suite would drive whichever was met first -- which is the
        // kind of failure that shows up as an unrelated test breaking months later.
        var titles = new[]
        {
            Adding.Title,
            Adding.ConfirmTitle,
            AgentsRegion.JoinTitle,
            Naming.CreateTitle,
            Naming.NameRunningTitle,
            Onboarding.Title,
            Participation.AnswerTitle,
            Participation.AskTitle,
            Participation.DeliveryTitle,
            Participation.TellTitle,
            Removing.Title,
            Removing.Destroy,
            Shell.ActionsTitle,
            Withdrawing.Title,
            Withdrawing.ReinstateTitle,
            Withdrawing.Retire,
            Withdrawing.Purge,
        };

        Assert.Equal(titles.Length, titles.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void No_dialog_is_titled_the_same_as_a_region()
    {
        // A dialog sharing a region's title would make `Find` return whichever the walk reached
        // first, which is a test driving the thing behind the dialog it meant to drive.
        var regions = new[]
        {
            ProjectsRegion.RegionTitle,
            AgentsRegion.RegionTitle,
            ConversationsRegion.RegionTitle,
            TranscriptRegion.RegionTitle,
        };

        var dialogs = new[]
        {
            Adding.Title,
            AgentsRegion.JoinTitle,
            Naming.CreateTitle,
            Onboarding.Title,
            Removing.Title,
            Withdrawing.Title,
        };

        Assert.Empty(regions.Intersect(dialogs, StringComparer.Ordinal));
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);
        shell.Focus(0);

        // Non-vacuity: there really are actions to collide, so an empty list above would mean the
        // check found nothing rather than that nothing is wrong.
        Assert.NotEmpty(shell.Actions());
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
