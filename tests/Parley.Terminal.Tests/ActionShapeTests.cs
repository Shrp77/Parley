using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

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
        using var shell = TerminalCommand.Build(directory, seats, manifestPath: "nowhere");

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
        using var shell = TerminalCommand.Build(directory, seats, manifestPath: "nowhere");

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
        using var shell = TerminalCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);

        // Project actions live on the projects view now, and agent actions inside a project. That
        // is the whole of 006's rearrangement, asserted rather than assumed.
        await Walking.GoAsync(shell, Viewing.Projects);

        Assert.Contains(shell.Actions(), action => action.Label == Naming.CreateTitle);
        Assert.Contains(shell.Actions(), action => action.Label == Removing.Title);
        Assert.DoesNotContain(shell.Actions(), action => action.Label == Onboarding.Title);

        await Walking.GoAsync(shell, Viewing.Project);

        // Both ways a project gains agents, from inside the project: a project is named first and agents are
        // enrolled into it, the owner's call of 2026-09-27.
        Assert.Contains(shell.Actions(), action => action.Label == Adding.Title);
        Assert.Contains(shell.Actions(), action => action.Label == Onboarding.Title);
    }

    [Fact]
    public void The_enrolment_window_names_the_project_first()
    {
        // The owner's wording, 2026-09-27: "[project name]: Enroll agents from folders".
        var alpha = new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null);

        Assert.Equal("alpha: Enroll agents from folders", Onboarding.Caption(alpha));
        Assert.Equal("Enroll agents from folders", Onboarding.Caption(null));
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
            Browsing.Name,
            Browsing.FolderName,
            Naming.CreateTitle,
            Onboarding.Title,
            Participation.ResultTitle,
            Participation.RequestTitle,
            Participation.StartTitle,
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
        using var shell = TerminalCommand.Build(directory, seats, manifestPath: "nowhere");

        driving.Show(shell);
        shell.Focus(0);

        // Non-vacuity: there really are actions to collide, so an empty list above would mean the
        // check found nothing rather than that nothing is wrong.
        Assert.NotEmpty(shell.Actions());
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
