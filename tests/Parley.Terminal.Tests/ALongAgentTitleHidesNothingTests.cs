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
/// That an agent's long words — a roadmap title, an ownership line, an answer — never push what a choice does, what it
/// records, or the proposal's own title off the screen.
/// </summary>
/// <remarks>
/// <b>Found by review, round 56.</b> Roadmap text has no bound, and a choice dialog cannot scroll and cuts its tail: the
/// decline door put the agent's title first, so a long one hid what declining does; the revise choice quoted the
/// matched roadmap's title whole, hiding that a revision replaces every item; and a long revise hint in the proposal's
/// heading hid the title a confirm records. Each sentence now quotes the agent's words short. Round 57 found that a
/// shortened title cannot tell apart two roadmaps that share its first eighty characters, so the revise choice names
/// the roadmap by number; that a long ownership line hid what retiring and purging do; and that an answer padded with
/// blank lines hid words Accept records.
/// </remarks>
public sealed class ALongAgentTitleHidesNothingTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private static readonly string Long = string.Concat(Enumerable.Repeat("Quarterly billing export ", 120)).Trim();

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public ALongAgentTitleHidesNothingTests()
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RoadmapRepository Repository => new(_connection);

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        _connection.Dispose();
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(80, 24)]
    public async Task The_decline_door_says_what_declining_does_whatever_the_title(int width, int height)
    {
        using var driving = Driving.Open(width: width, height: height);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        await ProposeAsync(Long);

        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        driving.Press(Key.D.WithAlt);
        await shell.Settled;

        _ = (ChoiceDialog)driving.Find(Proposing.DeclineTitle);
        driving.App.LayoutAndDraw(true);
        var screen = driving.Text();

        Assert.Contains("Proposed by orders@test", screen, StringComparison.Ordinal);
        Assert.Contains("Declining keeps no roadmap", screen, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(80, 24)]
    public async Task The_revise_choice_says_a_revision_replaces_every_item_whatever_the_title(int width, int height)
    {
        using var driving = Driving.Open(width: width, height: height);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        var first = await Repository.GetProposalAsync(await ProposeAsync(Long), Ct);
        Assert.True((await Repository.ConfirmAsync(first!.Id, first.Content, null, T0, cancellationToken: Ct)).Done);
        await ProposeAsync("Short", target: Long);

        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);

        _ = (ChoiceDialog)driving.Find(Proposing.Title);
        driving.App.LayoutAndDraw(true);
        var screen = driving.Text();

        Assert.Contains("replaces every item", screen.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("Make it a new roadmap", screen, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(80, 24)]
    public void A_long_revise_hint_leaves_the_proposals_title_in_view(int width, int height)
    {
        using var driving = Driving.Open(width: width, height: height);
        var content = new RoadmapContent(
            "Rotate every production key",
            [new RoadmapItemDraft(null, "Delete the export bucket", Billing, [])]);
        var proposal = new RoadmapProposal(1, Orders, null, Long, content, 10, ProposalState.Waiting, T0, null);

        Prompting.Open(driving.App, new ProposalView(proposal));
        driving.App.LayoutAndDraw(true);
        var screen = driving.Text();

        Assert.Contains(ProposalView.ConfirmLabel, screen, StringComparison.Ordinal);
        Assert.Contains("Rotate every production key", screen, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(80, 24)]
    public async Task The_revise_choice_names_which_of_two_like_titled_roadmaps_it_replaces(int width, int height)
    {
        using var driving = Driving.Open(width: width, height: height);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);
        var prefix = "Ship the quarterly billing export to every regional ledger and reconcile them all by";

        foreach (var title in new[] { prefix + " Friday, phase one", prefix + " Friday, phase two" })
        {
            var made = await Repository.GetProposalAsync(await ProposeAsync(title), Ct);
            Assert.True((await Repository.ConfirmAsync(made!.Id, made.Content, null, T0, cancellationToken: Ct)).Done);
        }

        await ProposeAsync("Short", target: prefix + " Friday, phase two");

        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);

        _ = (ChoiceDialog)driving.Find(Proposing.Title);
        driving.App.LayoutAndDraw(true);
        var flat = driving.Text().Replace("\n", " ", StringComparison.Ordinal);

        Assert.Contains("Revise roadmap #2", flat, StringComparison.Ordinal);
        Assert.Contains("replaces every item", flat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_revise_choice_says_it_also_settles_the_other_proposals_waiting_to_revise_that_roadmap()
    {
        // Round 58: a revision supersedes every other waiting proposal hinting at the roadmap, which are shown nowhere
        // after, and the choice did not say so.

        var made = await Repository.GetProposalAsync(await ProposeAsync("Billing export"), Ct);
        Assert.True((await Repository.ConfirmAsync(made!.Id, made.Content, null, T0, cancellationToken: Ct)).Done);

        // The two others name it in other cases, as the store's match ignores case; the newest is the one opened.
        await ProposeAsync("One way", target: "BILLING EXPORT");
        await ProposeAsync("Another way", target: "billing Export");
        await ProposeAsync("The one opened", target: "Billing export");

        Assert.Contains("A revision also settles 2 other proposals waiting to revise this roadmap", await ReviseChoiceAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_revise_choice_says_any_other_proposal_is_settled_when_the_title_is_longer_than_a_heading_shows()
    {
        // Round 59: the proposal list carries hints cut to 200 characters, so a longer title matched none of them and the
        // choice said nothing while the store superseded them all.
        var title = string.Concat(Enumerable.Repeat("Billing export ", 20)).Trim();
        var made = await Repository.GetProposalAsync(await ProposeAsync(title), Ct);
        Assert.True((await Repository.ConfirmAsync(made!.Id, made.Content, null, T0, cancellationToken: Ct)).Done);
        await ProposeAsync("One way", target: title);
        await ProposeAsync("The one opened", target: title);

        Assert.Contains("A revision also settles any other proposal waiting to revise this roadmap", await ReviseChoiceAsync(), StringComparison.Ordinal);
    }

    // Opens the proposals, opens the newest, chooses to confirm it, and returns what the revise choice says.
    private async Task<string> ReviseChoiceAsync()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;
        Assert.True(shell.Roadmaps.OpenSelected());
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.C.WithAlt, () => shell.Settled);

        return ((ChoiceDialog)driving.Find(Proposing.Title)).Described;
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(80, 24)]
    public async Task A_long_ownership_line_leaves_what_retiring_and_purging_do_in_view(int width, int height)
    {
        using var driving = Driving.Open(width: width, height: height);
        var owns = "Owns billing: " + string.Concat(Enumerable.Repeat("invoices, ledgers, exports, refunds, ", 40)).Trim();
        var row = new AgentRow("billing@test", "active", "pull", owns, "/repos/billing", false);
        var agent = new AgentRecord(Billing, "mcp", AgentTier.Pull, "/repos/billing", null, null, false, AgentStatus.Active, owns);

        await Withdrawing.OpenAsync(
            driving.App, new BrokerClient(_broker.Operators), row, agent, [], null, null, _ => { }, _ => Task.CompletedTask);

        driving.App.LayoutAndDraw(true);
        var flat = driving.Text().Replace("\n", " ", StringComparison.Ordinal);

        Assert.Contains("Retiring withdraws", flat, StringComparison.Ordinal);
        Assert.Contains("Purging deletes", flat, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", flat, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(80, 24)]
    public void An_answer_padded_with_blank_lines_shows_what_accept_records(int width, int height)
    {
        using var driving = Driving.Open(width: width, height: height);
        var answer = "I own billing and nothing else."
            + string.Concat(Enumerable.Repeat("\n", 40))
            + "Also orders; ask first.";
        var flattened = OwnershipStatement.Flatten(answer);
        var question = new OwnershipQuestion(Billing, "ownership-1", 1, QuestionState.Waiting, answer, flattened, "auto accept is off", T0, null);
        var agent = new AgentRecord(Billing, "mcp", AgentTier.Pull, "/repos/billing", null, null, false, AgentStatus.Active);

        // As OwnershipReview builds it.
        Prompting.Open(
            driving.App,
            Prompting.Choose(OwnershipReview.Title, OwnershipReview.Detail(agent, question), OwnershipReview.Choices(question)));
        driving.App.LayoutAndDraw(true);

        // Read as words: the dialog wraps the answer, and its border stands between the halves of a line.
        var flat = System.Text.RegularExpressions.Regex.Replace(driving.Text(), @"[\s┃│]+", " ");

        Assert.Contains(OwnershipReview.Accept, OwnershipReview.Choices(question));
        Assert.Contains("Also orders; ask first.", flat, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_already_accepted_names_no_choice_the_review_does_not_offer()
    {
        // Round 57: the review said "as Accept would record it" and pointed at Edit for an answer already accepted, where
        // neither is offered and the line is already recorded.
        var answer = string.Concat(Enumerable.Repeat("I own billing, invoices and refunds. ", 10)).Trim();
        var question = new OwnershipQuestion(
            Billing, "ownership-1", 1, QuestionState.Accepted, answer, OwnershipStatement.Flatten(answer), null, T0, null);
        var agent = new AgentRecord(Billing, "mcp", AgentTier.Pull, "/repos/billing", null, null, false, AgentStatus.Active);

        var detail = OwnershipReview.Detail(agent, question);

        Assert.DoesNotContain(OwnershipReview.Edit, OwnershipReview.Choices(question));
        Assert.DoesNotContain(OwnershipReview.Edit, detail, StringComparison.Ordinal);
        Assert.DoesNotContain("as Accept would record it", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(140, 40)]
    [InlineData(80, 24)]
    public void The_agents_own_page_says_what_save_records_whatever_its_answer(int width, int height)
    {
        // Round 57: the page an agent's line is changed on (Alt+n) drew the answer with its blank lines and every line
        // whole, so what Save records went below a page that cannot scroll.
        using var driving = Driving.Open(width: width, height: height);
        var answer = "I own billing and nothing else." + string.Concat(Enumerable.Repeat("\n", 40)) + "Also orders; ask first.";
        var question = new OwnershipQuestion(
            Billing, "ownership-1", 1, QuestionState.Waiting, answer, OwnershipStatement.Flatten(answer), "auto accept is off", T0, null);
        var owns = string.Concat(Enumerable.Repeat("invoices, ledgers, exports, refunds, ", 40)).Trim();
        var agent = new AgentRecord(Billing, "mcp", AgentTier.Pull, "/repos/billing", null, null, false, AgentStatus.Active, owns);

        Prompting.Open(
            driving.App,
            new ResponsibilityDialog(Responsibility.Described(agent, question, [("orders@test", owns)], null), owns, canSave: true));
        driving.App.LayoutAndDraw(true);

        var flat = System.Text.RegularExpressions.Regex.Replace(driving.Text(), @"[\s┃│]+", " ");

        Assert.Contains("Saving records the words as yours", flat, StringComparison.Ordinal);
    }

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    private async Task<long> ProposeAsync(string title, string? target = null)
    {
        var (id, problem) = await Repository.ProposeAsync(
            Orders,
            null,
            target,
            new RoadmapContent(
                title,
                [
                    new RoadmapItemDraft(null, "Add CSV export", Billing, []),
                    new RoadmapItemDraft(null, "Call it from the report", Orders, [1]),
                ]),
            T0,
            cancellationToken: Ct);

        Assert.Null(problem);

        return id!.Value;
    }

    private static async Task<Shell> OpenAsync(Driving.Session driving, BrokerDirectory directory)
    {
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };
        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        return shell;
    }
}
