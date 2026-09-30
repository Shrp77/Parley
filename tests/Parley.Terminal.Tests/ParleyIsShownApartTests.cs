using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the terminal shows Parley apart from agents and seats and offers nothing on it, and that the ownership review
/// and the Auto Accept switch are on the screen (SPEC.md principle 2.11, FR-005, FR-016 to FR-018).
/// </summary>
/// <remarks>
/// <b>Each was watched to fail with the filter or the action removed.</b> A row for Parley on the agents pane would
/// offer it retire, purge, instruct and "ask what it owns", each of which the broker refuses — and a project counting it
/// would say it had an agent on the day it was created.
/// </remarks>
public sealed class ParleyIsShownApartTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static AgentRecord Agent(string name, string adapter = "test", OwnershipSource? source = null) =>
        new(
            AgentName.Create(name),
            adapter,
            AgentTier.Pull,
            "/src/thing",
            null,
            null,
            false,
            AgentStatus.Active,
            "the thing",
            AgentPresence.Startable,
            source is null ? null : "the thing",
            source);

    private static AgentRecord Parley => Agent(Facilitation.On("test").Value, Facilitation.Adapter);

    [Fact]
    public async Task Parley_is_not_a_row_on_the_agents_pane()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent("orders@test"), Parley], NodeCatalogue.Empty, null);

        Assert.DoesNotContain(shell.Agents.Registry, record => record.Facilitator);
        Assert.DoesNotContain(shell.Agents.Standings, row => row.Name.StartsWith(Facilitation.Role, StringComparison.Ordinal));
        Assert.Contains(shell.Agents.Standings, row => row.Name == "orders@test");
    }

    [Fact]
    public void A_project_with_only_Parley_counts_no_agents()
    {
        var figures = ProjectFigures.Of([Parley], new ConversationPage([], null, 0));

        Assert.Equal(0, figures.Agents);
    }

    [Fact]
    public async Task The_review_is_offered_on_the_agents_pane_and_needs_no_seat()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent("orders@test")], NodeCatalogue.Empty, null);
        shell.Focus(2);

        Assert.Null(shell.Held);
        Assert.Contains(shell.Actions(), a => a.Label == OwnershipReview.Title);
    }

    [Fact]
    public void A_line_accepted_automatically_reads_differently_from_every_line_a_person_decided()
    {
        // FR-018: the review is only worth something if a person can see at a glance which lines nobody read.
        var automatic = OwnershipReview.Row(Agent("orders@test", source: OwnershipSource.Automatic), null);

        Assert.Contains(Provenance.Automatic, automatic, StringComparison.Ordinal);

        foreach (var decided in new[] { OwnershipSource.Proposed, OwnershipSource.Edited, OwnershipSource.Composed })
        {
            Assert.DoesNotContain(
                Provenance.Automatic,
                OwnershipReview.Row(Agent("orders@test", source: decided), null),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_review_offers_only_what_would_do_something()
    {
        OwnershipQuestion Question(QuestionState state) =>
            new(AgentName.Create("orders@test"), "owns-orders-1", 1, state, "Orders.", "Orders.", "because", DateTimeOffset.UnixEpoch, null);

        Assert.Contains(OwnershipReview.Accept, OwnershipReview.Choices(Question(QuestionState.Waiting)));
        Assert.Contains(OwnershipReview.Discard, OwnershipReview.Choices(Question(QuestionState.Waiting)));
        Assert.Contains(OwnershipReview.Reply, OwnershipReview.Choices(Question(QuestionState.AskedBack)));

        // An open question has nothing to accept, and asking again then would lose an answer on its way.
        Assert.DoesNotContain(OwnershipReview.AskAgain, OwnershipReview.Choices(Question(QuestionState.Asked)));
        Assert.DoesNotContain(OwnershipReview.Accept, OwnershipReview.Choices(Question(QuestionState.Accepted)));
        Assert.Contains(OwnershipReview.AskAgain, OwnershipReview.Choices(null));

        // Every dialog here opens on the answer that does nothing.
        Assert.All(
            new[] { null, Question(QuestionState.Waiting), Question(QuestionState.AskedBack) },
            q => Assert.Equal(OwnershipReview.Nothing, OwnershipReview.Choices(q)[0]));
    }

    [Fact]
    public void The_switch_says_what_it_is_now_before_offering_the_other()
    {
        // A switch that asked "turn it on?" without saying whether it was on would be a person deciding blind about the
        // one setting that decides whether anybody reads an agent's line before its peers do.
        Assert.Contains("Auto Accept is ON", AutoAccepting.Described("shop", on: true), StringComparison.Ordinal);
        Assert.Contains("Auto Accept is OFF", AutoAccepting.Described("shop", on: false), StringComparison.Ordinal);
        Assert.Contains("stay waiting", AutoAccepting.Described("shop", on: true), StringComparison.Ordinal);

        // The dialog opens on leaving it, and its two changes are told apart.
        Assert.NotEqual(AutoAccepting.TurnOn, AutoAccepting.TurnOff);
        Assert.DoesNotContain(AutoAccepting.Keep, new[] { AutoAccepting.TurnOn, AutoAccepting.TurnOff });
    }

    [Fact]
    public async Task A_project_with_nobody_to_review_says_so_and_reads_nothing()
    {
        // An empty project is an ordinary first afternoon, and asking the broker about nobody would be a round trip that
        // says nothing.
        using var driving = Driving.Open();
        using var directory = Directory();

        var said = string.Empty;
        var queued = 0;

        await OwnershipReview.OpenAsync(
            driving.App,
            new BrokerClient(directory.ClientFor(directory.Projects[0])),
            [Parley],
            "alpha",
            _ => queued++,
            reported =>
            {
                said = reported;

                return Task.CompletedTask;
            });

        Assert.Equal(OwnershipReview.NobodyToReview, said);
        Assert.Equal(0, queued);
    }

    [Fact]
    public void The_review_lists_neither_seats_nor_Parley_nor_the_retired()
    {
        var retired = Agent("inventory@test") with { Status = AgentStatus.Retired };

        var listed = OwnershipReview.Reviewable(
            [Agent("orders@test"), Agent("arvind@test", Seats.Adapter), Parley, retired]);

        Assert.Equal(["orders@test"], listed.Select(a => a.Name.Value));
    }
}
