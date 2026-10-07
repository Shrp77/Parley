using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Roadmaps;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Reviewing the roadmaps agents have proposed: confirm, edit then confirm, and decline (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven, and over a real socket</b>, because what is asserted is what the broker recorded after the dialogs: a
/// roadmap that exists and a proposer that was told. A test that read the dialog's text would show the surface agreed
/// with itself.
/// </para>
/// <para>
/// <b>Nothing here holds a seat.</b> Deciding a proposal speaks as nobody, so the shell is opened with no seat taken and
/// every assertion is about what the person did to the list.
/// </para>
/// </remarks>
public sealed class ProposingTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public ProposingTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RoadmapRepository Repository => new(_connection);

    private BrokerClient Client => new(_broker.Operators);

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    private async Task<long> ProposeAsync(string title = "Billing export", string? target = null)
    {
        var (id, problem) = await Repository.ProposeAsync(
            Orders,
            null,
            target,
            new RoadmapContent(title,
            [
                new RoadmapItemDraft(null, "Add CSV export", Billing, []),
                new RoadmapItemDraft(null, "Call it from the report", Orders, [1]),
            ]),
            T0,
            cancellationToken: Ct);

        Assert.Null(problem);

        return id!.Value;
    }

    private async Task<Shell> OpenAsync(Driving.Session driving, BrokerDirectory directory)
    {
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        return shell;
    }

    private static async Task OpenProposalsAsync(Shell shell)
    {
        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;
    }

    private static void Pick(Driving.Session driving, string title, string row)
    {
        var dialog = (ChoiceDialog)driving.Find(title);

        driving.Row(dialog.Choices, row);
        driving.Enter();
    }

    [Fact]
    public async Task The_proposals_are_offered_wherever_a_project_is_in_view()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        var offered = Assert.Single(shell.Global(), a => a.Label == Proposing.Title);

        // A chord, since a global letter would be shadowed by whichever pane already uses it.
        Assert.Equal(Shell.ProposalsKey, offered.Chord);
        Assert.Contains(Shell.ProposalsKey.ToString(), offered.Stroke, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_nothing_waiting_it_says_so_and_opens_nothing()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await OpenProposalsAsync(shell);

        Assert.Equal(Proposing.NoneWaiting, shell.Said);
    }

    [Fact]
    public async Task Confirming_as_proposed_makes_a_roadmap_that_began_as_proposed_and_tells_the_proposer()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        var proposal = await Repository.GetProposalAsync(await ProposeAsync(), Ct);

        await OpenProposalsAsync(shell);

        Pick(driving, Proposing.Title, Parley.Terminal.Safe.Text(ProposalText.Row(proposal!)));
        await shell.Settled;

        Pick(driving, Proposing.Title, Proposing.ConfirmAsProposed);
        await shell.Settled;

        var roadmap = Assert.Single(await Client.RoadmapsAsync(Ct));
        Assert.Equal("Billing export", roadmap.Title);

        var whole = await Client.RoadmapAsync(roadmap.Id, Ct);
        Assert.Equal(RoadmapOrigin.Proposed, whole!.Origin);
        Assert.Contains("Confirmed", shell.Said, StringComparison.Ordinal);

        var told = await Repository.DrainNoticesAsync(Orders, 10, T0, cancellationToken: Ct);
        Assert.Equal(ProposalState.Confirmed, Assert.Single(told).Outcome);
    }

    [Fact]
    public async Task Editing_first_confirms_the_persons_words_and_records_that_they_were_edited()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        var proposal = await Repository.GetProposalAsync(await ProposeAsync(), Ct);

        await OpenProposalsAsync(shell);

        Pick(driving, Proposing.Title, Parley.Terminal.Safe.Text(ProposalText.Row(proposal!)));
        await shell.Settled;
        Pick(driving, Proposing.Title, Proposing.EditThenConfirm);
        await shell.Settled;

        var form = (FormDialog)driving.Find(Proposing.Title);

        // The field opens holding the proposal as text, so editing it is typing over what is there.
        Assert.Contains("1. Add CSV export | for: billing@test", form.Values[0], StringComparison.Ordinal);

        form.Fill(0, "title: Billing export\n1. Add a CSV export endpoint | for: billing@test");
        form.Finish(taken: true);
        await shell.Settled;

        var roadmap = Assert.Single(await Client.RoadmapsAsync(Ct));
        var whole = await Client.RoadmapAsync(roadmap.Id, Ct);

        Assert.Equal(RoadmapOrigin.Edited, whole!.Origin);
        Assert.Equal(["Add a CSV export endpoint"], whole.Items.Select(i => i.Text));
    }

    [Fact]
    public async Task An_edit_with_a_dependency_that_points_the_wrong_way_is_refused_in_the_field()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        var proposal = await Repository.GetProposalAsync(await ProposeAsync(), Ct);

        await OpenProposalsAsync(shell);

        Pick(driving, Proposing.Title, Parley.Terminal.Safe.Text(ProposalText.Row(proposal!)));
        await shell.Settled;
        Pick(driving, Proposing.Title, Proposing.EditThenConfirm);
        await shell.Settled;

        var form = (FormDialog)driving.Find(Proposing.Title);

        // Item 1 needing item 2: the first item cannot need one that comes after it. The form refuses to go ahead, so
        // the broker is never the first to say so.
        form.Fill(0, "title: P\n1. First | needs: 2\n2. Second");
        form.Finish(taken: true);
        await shell.Settled;

        Assert.Empty(await Client.RoadmapsAsync(Ct));
        Assert.Single(await Client.ProposalsAsync(ProposalState.Waiting, Ct));
    }

    [Fact]
    public async Task Declining_sits_behind_its_description_and_tells_the_proposer()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        var proposal = await Repository.GetProposalAsync(await ProposeAsync(), Ct);

        await OpenProposalsAsync(shell);

        Pick(driving, Proposing.Title, Parley.Terminal.Safe.Text(ProposalText.Row(proposal!)));
        await shell.Settled;
        Pick(driving, Proposing.Title, Proposing.Decline);
        await shell.Settled;

        // The deliberate act: a second dialog that says what declining does, with the safe answer first.
        var asking = (ChoiceDialog)driving.Find(Proposing.DeclineTitle);

        Assert.Contains("is told", asking.Described, StringComparison.Ordinal);
        Assert.Equal(Proposing.Leave, asking.Choices.Source!.ToList()[0]?.ToString());

        // Still waiting until the second answer.
        Assert.Single(await Client.ProposalsAsync(ProposalState.Waiting, Ct));

        driving.Row(asking.Choices, Proposing.Decline);
        driving.Enter();
        await shell.Settled;

        Assert.Empty(await Client.ProposalsAsync(ProposalState.Waiting, Ct));
        Assert.Empty(await Client.RoadmapsAsync(Ct));

        var told = await Repository.DrainNoticesAsync(Orders, 10, T0, cancellationToken: Ct);
        Assert.Equal(ProposalState.Declined, Assert.Single(told).Outcome);
    }

    [Fact]
    public async Task Leaving_a_proposal_alone_leaves_it_waiting()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        var proposal = await Repository.GetProposalAsync(await ProposeAsync(), Ct);

        await OpenProposalsAsync(shell);

        Pick(driving, Proposing.Title, Parley.Terminal.Safe.Text(ProposalText.Row(proposal!)));
        await shell.Settled;

        // Enter on the first row, which is the safe one, changes nothing.
        driving.Enter();
        await shell.Settled;

        Assert.Equal(Proposing.Left, shell.Said);
        Assert.Single(await Client.ProposalsAsync(ProposalState.Waiting, Ct));
        Assert.Empty(await Client.RoadmapsAsync(Ct));
    }

    [Fact]
    public async Task A_title_that_matches_a_roadmap_offers_a_revision_and_a_new_roadmap_and_the_person_chooses()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        var first = await Repository.GetProposalAsync(await ProposeAsync("Billing export"), Ct);
        await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct);

        var revision = await Repository.GetProposalAsync(await ProposeAsync("Billing export v2", target: "billing export"), Ct);

        await OpenProposalsAsync(shell);

        Pick(driving, Proposing.Title, Parley.Terminal.Safe.Text(ProposalText.Row(revision!)));
        await shell.Settled;
        Pick(driving, Proposing.Title, Proposing.ConfirmAsProposed);
        await shell.Settled;

        // Shown with the match, and nothing is decided for the person (spec FR-009b).
        var choosing = (ChoiceDialog)driving.Find(Proposing.Title);

        Assert.Contains("matches the roadmap 'Billing export'", choosing.Described, StringComparison.Ordinal);
        Assert.Equal(Proposing.NewRoadmap, choosing.Choices.Source!.ToList()[0]?.ToString());

        driving.Row(choosing.Choices, "Revise the roadmap 'Billing export'");
        driving.Enter();
        await shell.Settled;

        var roadmaps = await Client.RoadmapsAsync(Ct);

        Assert.Single(roadmaps);
        Assert.Equal(2, roadmaps[0].Revision);
        Assert.Equal("Billing export v2", roadmaps[0].Title);
    }

    [Fact]
    public async Task No_seat_is_taken_to_decide_a_proposal()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await ProposeAsync();
        await OpenProposalsAsync(shell);

        // Deciding speaks as nobody (principle 2.12), so the review is available to a person who has not connected.
        Assert.Null(shell.Held);
        Assert.NotNull(driving.Find(Proposing.Title));
    }

    [Fact]
    public void The_projects_row_counts_what_is_proposed_and_leaves_an_unknown_count_out()
    {
        var page = new ConversationPage([], null, 0);

        Assert.EndsWith("· 2 roadmaps proposed, for you to decide", ProjectFigures.Of([], page, proposals: 2).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("proposed", ProjectFigures.Of([], page, proposals: 0).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("proposed", ProjectFigures.Of([], page, proposals: null).ToString(), StringComparison.Ordinal);
    }
}
