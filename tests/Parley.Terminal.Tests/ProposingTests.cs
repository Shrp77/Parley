using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Terminal.Gui.Input;
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

    /// <summary>Starts a conversation, because a proposal can only name one that exists.</summary>
    private async Task StartConversationAsync(string name)
    {
        var speaking = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await speaking.StartAsync(Orders, T0, Ct);

        var sent = await speaking.RequestAsync(
            Orders, Billing, "model-a", $$"""{"ask":"how do we export, in {{name}}?"}""", T0, name, Ct);

        Assert.True(sent.Exchange is not null, $"the request was refused: {sent.Admission.Rejection}");
    }

    private async Task<long> ProposeAsync(
        string title = "Billing export", string? target = null, string? source = null)
    {
        var (id, problem) = await Repository.ProposeAsync(
            Orders,
            source,
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

    /// <summary>Chooses a row as Enter does, without the draw a key press makes.</summary>
    /// <remarks>
    /// For a choice that starts queued work which opens another view: a press would draw on this thread while that work
    /// opens it (CLAUDE.md, dialogs open on the thread that draws). Await <c>shell.Settled</c> after.
    /// </remarks>
    private static void Accept(Driving.Session driving, string title, string row)
    {
        var dialog = (ChoiceDialog)driving.Find(title);

        driving.Row(dialog.Choices, row);
        dialog.Choices.InvokeCommand(Command.Accept);
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

        // The proposals are a pane of the project, and Enter on one opens it. Called as Enter's handler rather than pressed:
        // opening reads the proposal whole first, and a press would draw here while that work opens the view.
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;

        // By the key written on the button, which is the way the view says to choose.
        await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);

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

        // The proposals are a pane of the project, and Enter on one opens it. Called as Enter's handler rather than pressed:
        // opening reads the proposal whole first, and a press would draw here while that work opens the view.
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.E.WithAlt, () => shell.Settled);

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

        // The proposals are a pane of the project, and Enter on one opens it. Called as Enter's handler rather than pressed:
        // opening reads the proposal whole first, and a press would draw here while that work opens the view.
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.E.WithAlt, () => shell.Settled);

        var form = (FormDialog)driving.Find(Proposing.Title);

        // Item 1 needing item 2: the first item cannot need one that comes after it. The form refuses to go ahead, so
        // the broker is never the first to say so.
        form.Fill(0, "title: P\n1. First | needs: 2\n2. Second");
        form.Finish(taken: true);
        await shell.Settled;

        Assert.Empty(await Client.RoadmapsAsync(Ct));
        Assert.Single(await Client.ProposalsAsync(ProposalState.Waiting, cancellationToken: Ct));
    }

    [Fact]
    public async Task Declining_sits_behind_its_description_and_tells_the_proposer()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        var proposal = await Repository.GetProposalAsync(await ProposeAsync(), Ct);

        await OpenProposalsAsync(shell);

        // The proposals are a pane of the project, and Enter on one opens it. Called as Enter's handler rather than pressed:
        // opening reads the proposal whole first, and a press would draw here while that work opens the view.
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.D.WithAlt, () => shell.Settled);

        // The deliberate act: a second dialog that says what declining does, with the safe answer first.
        var asking = (ChoiceDialog)driving.Find(Proposing.DeclineTitle);

        Assert.Contains("is told", asking.Described, StringComparison.Ordinal);
        Assert.Equal(Proposing.Leave, asking.Choices.Source!.ToList()[0]?.ToString());

        // Still waiting until the second answer.
        Assert.Single(await Client.ProposalsAsync(ProposalState.Waiting, cancellationToken: Ct));

        driving.Row(asking.Choices, Proposing.Decline);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Empty(await Client.ProposalsAsync(ProposalState.Waiting, cancellationToken: Ct));
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

        // The proposals are a pane of the project, and Enter on one opens it. Called as Enter's handler rather than pressed:
        // opening reads the proposal whole first, and a press would draw here while that work opens the view.
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;

        // Escape leaves it, as it does everywhere on this surface, and changes nothing.
        await driving.PressThenSettleAsync(Key.Esc, () => shell.Settled);

        Assert.Equal(Proposing.Left, shell.Said);
        Assert.Single(await Client.ProposalsAsync(ProposalState.Waiting, cancellationToken: Ct));
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

        // The proposals are a pane of the project, and Enter on one opens it. Called as Enter's handler rather than pressed:
        // opening reads the proposal whole first, and a press would draw here while that work opens the view.
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);

        // Shown with the match, and nothing is decided for the person (spec FR-009b).
        var choosing = (ChoiceDialog)driving.Find(Proposing.Title);

        Assert.Contains("matches roadmap #1, 'Billing export'", choosing.Described, StringComparison.Ordinal);
        Assert.Equal(Proposing.NewRoadmap, choosing.Choices.Source!.ToList()[0]?.ToString());

        driving.Row(choosing.Choices, "Revise roadmap #1");
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var roadmaps = await Client.RoadmapsAsync(Ct);

        Assert.Single(roadmaps);
        Assert.Equal(2, roadmaps[0].Revision);
        Assert.Equal("Billing export v2", roadmaps[0].Title);
    }

    [Fact]
    public async Task A_revision_that_removes_open_work_is_described_and_confirmed_only_on_a_second_choice()
    {
        // A proposal's items carry no item numbers, so confirming it as a revision replaces every item. Item 1's request
        // is open, and its agent would not be told. Must fail against confirming as soon as the revision is chosen.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        var first = await Repository.GetProposalAsync(await ProposeAsync("Billing export"), Ct);
        await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct);

        var person = (await new AgentRepository(_connection).GetAllAsync(Ct)).Single(a => PersonSeat.Is(a.Name)).Name;
        var speaking = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
        await speaking.StartAsync(person, T0, Ct);
        var opened = await speaking.RequestAsync(person, Billing, "model-a", """{"ask":"Add the CSV export."}""", T0, "rm1-1", Ct);
        Assert.True(opened.Exchange is not null, $"the item could not be started: {opened.Admission.Rejection}");

        await ProposeAsync("Billing export", target: "billing export");

        async Task ChooseReviseAsync()
        {
            await OpenProposalsAsync(shell);
            Assert.True(shell.Roadmaps.OpenSelected());
            await shell.Settled;
            await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);
            Accept(driving, Proposing.Title, "Revise roadmap #1");
            await shell.Settled;
        }

        await ChooseReviseAsync();

        var warning = (ChoiceDialog)driving.Find(Proposing.Title);
        Assert.Contains("item 1, \"Add CSV export\", in rm1-1", warning.Described, StringComparison.Ordinal);
        Assert.Equal(1, (await Client.RoadmapsAsync(Ct)).Single().Revision);

        Accept(driving, Proposing.Title, Proposing.Leave);
        await shell.Settled;
        Assert.Equal(1, (await Client.RoadmapsAsync(Ct)).Single().Revision);

        await ChooseReviseAsync();
        Accept(driving, Proposing.Title, Proposing.RemoveAndConfirm);
        await shell.Settled;
        Assert.Equal(2, (await Client.RoadmapsAsync(Ct)).Single().Revision);
    }

    [Fact]
    public async Task Escape_on_a_new_roadmap_or_a_revision_confirms_neither()
    {
        // Escape leaves a proposal as it is in every other dialog. Must fail against Escape falling through to a new
        // roadmap, which spends the proposal on the choice the person did not make.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        var first = await Repository.GetProposalAsync(await ProposeAsync("Billing export"), Ct);
        await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct);
        var second = await ProposeAsync("Billing export", target: "billing export");

        await OpenProposalsAsync(shell);
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);
        Assert.IsType<ChoiceDialog>(driving.Find(Proposing.Title));

        await driving.PressThenSettleAsync(Key.Esc, () => shell.Settled);

        Assert.Single(await Client.RoadmapsAsync(Ct));
        Assert.Equal(ProposalState.Waiting, (await Repository.GetProposalAsync(second, Ct))!.State);
    }

    /// <summary>A channel that fails the one-roadmap read and passes everything else.</summary>
    private sealed class FailingRoadmapRead() : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath == "/parley.v1.Operators/Roadmap"
                ? throw new HttpRequestException("the roadmap read failed")
                : base.SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task A_revision_whose_roadmap_cannot_be_read_is_not_confirmed()
    {
        // Unread is not empty: the open work a revision removes cannot be described, so nothing is confirmed, as on the
        // web. Must fail against treating a failed read as nothing open.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = new BrokerDirectory(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address, new GrpcChannelOptions { HttpHandler = new FailingRoadmapRead() }));
        using var shell = await OpenAsync(driving, directory);

        var first = await Repository.GetProposalAsync(await ProposeAsync("Billing export"), Ct);
        await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct);
        await ProposeAsync("Billing export", target: "billing export");

        await OpenProposalsAsync(shell);
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);
        Accept(driving, Proposing.Title, "Revise roadmap #1");
        await shell.Settled;

        Assert.Equal(1, (await Client.RoadmapsAsync(Ct)).Single().Revision);
        Assert.Contains("nothing was confirmed", shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_reads_headings_and_one_proposal_is_read_whole_when_opened()
    {
        // A count and a list run on every refresh, and roadmap text has no bound, so they read headings: no item text,
        // the title cut short, the item count kept. Must fail against a list that carries the items, which makes every
        // refresh as large as everything one agent wrote.
        var (id, problem) = await Repository.ProposeAsync(
            Orders,
            null,
            null,
            new RoadmapContent(new string('T', 1_000), [new RoadmapItemDraft(null, new string('x', 100_000), null, []), new RoadmapItemDraft(null, "Two", null, [])]),
            T0,
            cancellationToken: Ct);
        Assert.Null(problem);

        var headings = await Client.ProposalsAsync(ProposalState.Waiting, headings: true, Ct);
        var heading = Assert.Single(headings);

        Assert.Empty(heading.Content.Items);
        Assert.Equal(2, heading.ItemCount);
        Assert.Equal(RoadmapProposal.HeadingLength, heading.Content.Title.Length);
        Assert.Equal(1, await Client.ProposalCountAsync(ProposalState.Waiting, Ct));

        var whole = await Client.ProposalAsync(id!.Value, Ct);

        Assert.NotNull(whole);
        Assert.Equal(1_000, whole.Content.Title.Length);
        Assert.Equal(100_000, whole.Content.Items[0].Text.Length);
        Assert.Null(whole.ItemCount);
    }

    [Fact]
    public async Task However_many_proposals_an_agent_makes_a_list_carries_the_newest_and_a_count_says_how_many()
    {
        // Nothing bounds how many proposals one agent makes, and a count and a list are read on every refresh. Must fail
        // against a list with no limit, or a count taken by downloading the list.
        var made = RoadmapProposal.HeadingLimit + 5;

        for (var n = 1; n <= made; n++)
        {
            var (_, problem) = await Repository.ProposeAsync(
                Orders, null, null, new RoadmapContent($"Plan {n}", [new RoadmapItemDraft(null, "Do it", null, [])]), T0,
                cancellationToken: Ct);

            Assert.Null(problem);
        }

        var headings = await Client.ProposalsAsync(ProposalState.Waiting, headings: true, Ct);

        Assert.Equal(RoadmapProposal.HeadingLimit, headings.Count);
        Assert.Equal($"Plan {made}", headings[0].Content.Title);
        Assert.Equal(made, await Client.ProposalCountAsync(ProposalState.Waiting, Ct));
    }

    [Fact]
    public async Task Waiting_proposals_larger_than_a_default_reply_can_still_be_read()
    {
        // Roadmap text has no bound, so one agent's proposals can outgrow gRPC's default 4 MB reply. Through the channel
        // every surface uses, the person must still read them. Must fail against a channel with the default limit.
        foreach (var n in new[] { 1, 2 })
        {
            var (id, problem) = await Repository.ProposeAsync(
                Orders, null, null, new RoadmapContent($"Plan {n}", [new RoadmapItemDraft(null, new string('x', 3_000_000), null, [])]), T0,
                cancellationToken: Ct);

            Assert.Null(problem);
            Assert.NotNull(id);
        }

        using var channel = BrokerDirectory.Connect(_broker.Address);
        var waiting = await new BrokerClient(new Parley.Transport.Operators.OperatorsClient(channel))
            .ProposalsAsync(ProposalState.Waiting, cancellationToken: Ct);

        Assert.Equal(2, waiting.Count);
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
        Assert.Single(shell.Roadmaps.Proposals);
    }

    [Fact]
    public void The_projects_row_counts_what_is_proposed_and_leaves_an_unknown_count_out()
    {
        var page = new ConversationPage([], null, 0);

        Assert.EndsWith("· 2 roadmaps proposed, for you to decide", ProjectFigures.Of([], page, proposals: 2).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("proposed", ProjectFigures.Of([], page, proposals: 0).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("proposed", ProjectFigures.Of([], page, proposals: null).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proposal_from_the_conversation_a_person_is_in_opens_straight_to_its_view()
    {
        // The report: a person had to leave the conversation for the project overview to decide a roadmap agreed in it. Must
        // fail against the chord being withheld in a conversation, or against a list being put in front of the one thing.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await StartConversationAsync("some-discussion");
        await StartConversationAsync("elsewhere");
        await ProposeAsync(source: "some-discussion");
        await ProposeAsync("Unrelated", source: "elsewhere");
        await Walking.GoAsync(shell, Viewing.Conversation, "some-discussion");

        await OpenProposalsAsync(shell);

        var view = Assert.IsType<ProposalView>(driving.Find(Proposing.Title));

        Assert.Contains("Billing export", view.Header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_a_conversation_with_no_proposal_of_its_own_the_list_is_shown_as_before()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await StartConversationAsync("elsewhere");
        await ProposeAsync(source: "elsewhere");
        await Walking.GoAsync(shell, Viewing.Conversation, "some-discussion");

        await OpenProposalsAsync(shell);

        Assert.IsType<ChoiceDialog>(driving.Find(Proposing.Title));
    }

    [Fact]
    public async Task The_proposal_view_shows_the_roadmap_who_it_names_and_every_choice_at_once()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        var proposal = await Repository.GetProposalAsync(await ProposeAsync(), Ct);

        var view = new ProposalView(proposal!);

        Prompting.Open(driving.App, view);

        var screen = driving.Text();

        Assert.Contains("1. Add CSV export", screen, StringComparison.Ordinal);
        Assert.Contains("billing@test", screen, StringComparison.Ordinal);
        Assert.Contains(ProposalView.ConfirmLabel, screen, StringComparison.Ordinal);
        Assert.Contains(ProposalView.EditLabel, screen, StringComparison.Ordinal);
        Assert.Contains(ProposalView.DeclineLabel, screen, StringComparison.Ordinal);
        Assert.Contains(ProposalView.LeaveLabel, screen, StringComparison.Ordinal);
        Assert.Contains("Up and Down scroll", screen, StringComparison.Ordinal);
        Assert.StartsWith("Up and Down scroll", ProposalView.Hint, StringComparison.Ordinal);

        // Who the proposal names, grouped by agent, so which agent is meant for what reads from either side.
        var agents = view.AgentsText.ReplaceLineEndings("|");

        Assert.Contains("billing@test|  item 1", agents, StringComparison.Ordinal);
        Assert.Contains("orders@test|  item 2", agents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_roadmap_longer_than_the_screen_scrolls_to_its_last_item()
    {
        // The report: it did not scroll. Must fail against a description in a menu, which has one screenful and no more.
        using var driving = Driving.Open(width: 120, height: 30);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        var items = Enumerable.Range(1, 40)
            .Select(n => new RoadmapItemDraft(null, $"Step number {n} of the long roadmap", Billing, []))
            .ToList();

        var (id, problem) = await Repository.ProposeAsync(Orders, null, null, new RoadmapContent("Long", items), T0, cancellationToken: Ct);

        Assert.Null(problem);

        var proposal = await Repository.GetProposalAsync(id!.Value, Ct);

        Prompting.Open(driving.App, new ProposalView(proposal!));

        Assert.DoesNotContain("40. Step number 40", driving.Text(), StringComparison.Ordinal);

        for (var press = 0; press < 160; press++)
        {
            driving.Press(Key.CursorDown);
        }

        driving.App.LayoutAndDraw(true);

        Assert.Contains("40. Step number 40", driving.Text(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_buttons_key_chooses_whether_the_roadmap_or_a_button_has_focus(bool onTheButtons)
    {
        // Must fail against the roadmap eating the letter: a focused view is offered a key before the dialog around it.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        var proposal = await Repository.GetProposalAsync(await ProposeAsync(), Ct);

        var chosen = new List<ProposalChoice>();

        foreach (var (key, expected) in new[]
                 {
                     (Key.C.WithAlt, ProposalChoice.Confirm),
                     (Key.E.WithAlt, ProposalChoice.Edit),
                     (Key.D.WithAlt, ProposalChoice.Decline),
                     (Key.Esc, ProposalChoice.Leave),
                 })
        {
            var view = new ProposalView(proposal!);

            view.Done += (_, choice) => chosen.Add(choice);

            var token = Prompting.Open(driving.App, view);

            if (onTheButtons)
            {
                driving.Tab();
                driving.Tab();
            }

            driving.Press(key);

            Prompting.Close(driving.App, token);

            Assert.Equal(expected, chosen[^1]);
        }
    }

    [Fact]
    public void An_agents_words_are_neutralised_before_they_are_drawn_in_the_view()
    {
        using var driving = Driving.Open(width: 140, height: 40);

        var content = new RoadmapContent(
            "Billing" + (char)0x1b + "[2J",
            [new RoadmapItemDraft(null, "Do it" + (char)0x1b + "[31m", Billing, [])]);

        var proposal = new RoadmapProposal(1, Orders, null, null, content, 10, ProposalState.Waiting, T0, null);

        var view = new ProposalView(proposal);

        Assert.DoesNotContain((char)0x1b, view.Header);
        Assert.DoesNotContain((char)0x1b, view.ItemsText);
    }

    [Fact]
    public async Task What_is_proposed_is_on_the_status_line_with_the_key_that_opens_it()
    {
        // Must fail against the count being known only to the projects list: in a conversation nothing said a roadmap had
        // been put forward.
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var seats = new Parley.Client.Seating.SeatBook(directory.AgentsFor);

        await StartConversationAsync("some-discussion");
        await ProposeAsync(source: "some-discussion");

        using var shell = new Shell(directory) { Seats = seats, MailEvery = TimeSpan.FromMilliseconds(100), FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Conversation, "some-discussion");

        await Eventually.UntilAsync(() => shell.Proposals == 1, "the count of proposals was never read", Ct);

        await shell.Settled;
        driving.App.LayoutAndDraw(true);

        Assert.Contains("1 roadmap proposed (F4)", shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proposed_roadmap_is_listed_in_the_project_view_and_only_while_one_is_waiting()
    {
        // The report: "if it's a proposed roadmap, it should be listed in the Project view". Must fail against a menu that
        // opens a pop-up, and against an empty pane taking a third of the screen in a project with nothing proposed.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        Assert.DoesNotContain(shell.Roadmaps, shell.Panes);

        await ProposeAsync();
        await OpenProposalsAsync(shell);
        driving.App.LayoutAndDraw(true);

        Assert.Contains(shell.Roadmaps, shell.Panes);
        Assert.Contains(RoadmapsRegion.RegionTitle, driving.Text(), StringComparison.Ordinal);
        Assert.Contains("Billing export", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_menu_entry_goes_to_the_pane_rather_than_opening_a_pop_up_list()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await ProposeAsync();
        await OpenProposalsAsync(shell);

        Assert.Same(shell.Roadmaps, shell.Panes[shell.FocusedRegion]);
        // The pane carries the same title, so a title search cannot tell; what is on top of the screen can.
        Assert.IsNotType<ChoiceDialog>(driving.App.TopRunnableView);
    }

    [Fact]
    public async Task A_decided_proposal_leaves_the_pane_and_the_pane_leaves_the_view_when_it_was_the_last()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await ProposeAsync();
        await OpenProposalsAsync(shell);

        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);

        Assert.Empty(shell.Roadmaps.Proposals);
        Assert.DoesNotContain(shell.Roadmaps, shell.Panes);
    }

    [Fact]
    public async Task Enter_on_the_roadmap_does_not_confirm_it()
    {
        // The first button was the default and the roadmap does not take Enter, so Enter with the roadmap in front of
        // you confirmed it. Real data showed it: Enter gave Confirm. Must fail against a default button.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        var proposal = await Repository.GetProposalAsync(await ProposeAsync(), Ct);

        var view = new ProposalView(proposal!);
        var chose = new List<ProposalChoice>();

        view.Done += (_, choice) => chose.Add(choice);

        Prompting.Open(driving.App, view);
        driving.Enter();

        Assert.Empty(chose);
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(100, 30)]
    [InlineData(80, 24)]
    public async Task Every_button_is_on_screen_at_the_sizes_a_terminal_has(int width, int height)
    {
        // Real data at eighty columns drew "Decline" cut off and no "Leave" at all.
        using var driving = Driving.Open(width: width, height: height);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        var proposal = await Repository.GetProposalAsync(await ProposeAsync(), Ct);

        Prompting.Open(driving.App, new ProposalView(proposal!));

        var screen = driving.Text();

        foreach (var label in new[] { ProposalView.ConfirmLabel, ProposalView.EditLabel, ProposalView.DeclineLabel, ProposalView.LeaveLabel })
        {
            Assert.Contains(label, screen, StringComparison.Ordinal);
        }
    }
}
