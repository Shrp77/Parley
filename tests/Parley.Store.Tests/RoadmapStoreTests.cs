using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That storage holds what makes a roadmap the person's, and that the repository writes it through one path
/// (SPEC.md §5.7, §9.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Direct SQL for the refusals, for the reason <see cref="FacilitatorStore"/> gives.</b> These are about what
/// storage refuses, so they reach it without passing through any code that might refuse first. The repository
/// tests below are about what it writes.
/// </para>
/// <para>
/// <b>Each rule was watched to fail with the rule removed.</b> The reserved-name test carries its own proof: with the
/// trigger dropped, the same open by an agent is admitted, so the refusal is the trigger's and not an accident of
/// the fixture.
/// </para>
/// </remarks>
public sealed class RoadmapStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly FacilitatorStore _store = new();

    public void Dispose() => _store.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RoadmapRepository Repository => new(_store.Connection);

    private static AgentName Orders => AgentName.Create("orders@test");

    private static AgentName Billing => AgentName.Create("billing@test");

    private static RoadmapItemDraft Item(string text, AgentName? agent = null, params int[] needs) =>
        new(null, text, agent, needs);

    private static RoadmapContent Content(string title, params RoadmapItemDraft[] items) => new(title, items);

    private async Task<long> ProposedAsync(string title = "Billing export", AgentName? by = null, string? target = null)
    {
        var (id, problem) = await Repository.ProposeAsync(
            by ?? Orders,
            null,
            target,
            Content(title, Item("Add CSV export", Billing), Item("Call it from the report", Orders, 1), Item("Document the column", null, 2)),
            Now,
            cancellationToken: Ct);

        Assert.Null(problem);

        return id!.Value;
    }

    private SqliteException Refused(string sql) =>
        Assert.Throws<SqliteException>(() => _store.Execute(sql));

    // ---- a proposal is not a roadmap

    [Fact]
    public async Task A_proposal_writes_no_message_opens_no_exchange_and_creates_no_roadmap()
    {
        await ProposedAsync();

        Assert.Equal(0L, _store.Count("SELECT count(*) FROM messages;"));
        Assert.Equal(0L, _store.Count("SELECT count(*) FROM exchanges;"));
        Assert.Equal(0L, _store.Count("SELECT count(*) FROM conversations;"));
        Assert.Equal(0L, _store.Count("SELECT count(*) FROM roadmaps;"));
        Assert.Equal(1L, _store.Count("SELECT count(*) FROM roadmap_proposals WHERE state = 'waiting';"));
    }

    [Fact]
    public async Task A_proposal_is_read_back_as_written_with_its_size_recorded()
    {
        var id = await ProposedAsync();

        var proposal = await Repository.GetProposalAsync(id, Ct);

        Assert.NotNull(proposal);
        Assert.Equal(ProposalState.Waiting, proposal.State);
        Assert.Equal("Billing export", proposal.Content.Title);
        Assert.Equal(["Add CSV export", "Call it from the report", "Document the column"],
            proposal.Content.Items.Select(i => i.Text));
        Assert.Equal(Billing, proposal.Content.Items[0].IntendedAgent);
        Assert.Equal([1], proposal.Content.Items[1].Needs);
        Assert.Equal([2], proposal.Content.Items[2].Needs);
        Assert.True(proposal.Size > 0);
    }

    [Theory]
    [InlineData("human-user@test")]
    [InlineData("parley-facilitator@test")]
    public void The_person_and_Parley_cannot_propose(string proposer)
    {
        var refused = Refused($"""
            INSERT INTO roadmap_proposals (proposer, title, size, created_utc)
            VALUES ('{proposer}', 'A roadmap', 9, '{FacilitatorStore.Stamp}');
            """);

        Assert.Contains("proposed by an agent", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_can_propose_which_is_what_makes_the_refusal_above_the_triggers()
    {
        _store.Execute($"""
            INSERT INTO roadmap_proposals (proposer, title, size, created_utc)
            VALUES ('orders@test', 'A roadmap', 9, '{FacilitatorStore.Stamp}');
            """);

        Assert.Equal(1L, _store.Count("SELECT count(*) FROM roadmap_proposals;"));
    }

    [Fact]
    public async Task A_proposal_with_no_items_or_a_line_break_is_refused_before_anything_is_written()
    {
        var empty = await Repository.ProposeAsync(Orders, null, null, Content("Plan"), Now, cancellationToken: Ct);
        var broken = await Repository.ProposeAsync(
            Orders, null, null, Content("Plan", Item("two\nlines")), Now, cancellationToken: Ct);

        Assert.NotNull(empty.Problem);
        Assert.NotNull(broken.Problem);
        Assert.Equal(0L, _store.Count("SELECT count(*) FROM roadmap_proposals;"));
    }

    [Fact]
    public async Task A_proposal_cannot_name_an_existing_item()
    {
        var named = await Repository.ProposeAsync(
            Orders, null, null, Content("Plan", new RoadmapItemDraft(1, "Do it", null, [])), Now,
            cancellationToken: Ct);

        Assert.NotNull(named.Problem);
    }

    [Fact]
    public void A_title_or_an_item_with_a_line_break_is_refused_by_storage_as_well()
    {
        Refused($"""
            INSERT INTO roadmap_proposals (proposer, title, size, created_utc)
            VALUES ('orders@test', 'one' || char(10) || 'two', 9, '{FacilitatorStore.Stamp}');
            """);

        _store.Execute($"""
            INSERT INTO roadmap_proposals (id, proposer, title, size, created_utc)
            VALUES (7, 'orders@test', 'fine', 9, '{FacilitatorStore.Stamp}');
            """);

        Refused("INSERT INTO roadmap_proposal_items (proposal_id, position, text) VALUES (7, 1, 'a' || char(13) || 'b');");
    }

    [Fact]
    public void A_proposals_dependency_may_only_point_backwards()
    {
        _store.Execute($"""
            INSERT INTO roadmap_proposals (id, proposer, title, size, created_utc)
            VALUES (7, 'orders@test', 'fine', 9, '{FacilitatorStore.Stamp}');
            INSERT INTO roadmap_proposal_items (proposal_id, position, text) VALUES (7, 1, 'a'), (7, 2, 'b');
            """);

        Refused("INSERT INTO roadmap_proposal_needs (proposal_id, position, needs_position) VALUES (7, 1, 2);");
        Refused("INSERT INTO roadmap_proposal_needs (proposal_id, position, needs_position) VALUES (7, 2, 2);");
        _store.Execute("INSERT INTO roadmap_proposal_needs (proposal_id, position, needs_position) VALUES (7, 2, 1);");
    }

    // ---- confirming

    [Fact]
    public async Task Confirming_a_proposal_unchanged_makes_a_roadmap_that_began_as_proposed()
    {
        var id = await ProposedAsync();
        var proposal = await Repository.GetProposalAsync(id, Ct);

        var change = await Repository.ConfirmAsync(id, proposal!.Content, null, Now, cancellationToken: Ct);

        Assert.True(change.Done, change.Problem);
        Assert.Equal(RoadmapOrigin.Proposed, change.Origin);
        Assert.Equal(1, change.Revision);

        var roadmap = await Repository.GetAsync(change.RoadmapId!.Value, Ct);

        Assert.NotNull(roadmap);
        Assert.Equal(1, roadmap.Number);
        Assert.Equal("orders@test", roadmap.Proposer);
        Assert.Equal(["rm1-1", "rm1-2", "rm1-3"], roadmap.Items.Select(i => i.ConversationName));
        Assert.Equal([1], roadmap.Items[1].Needs);
        Assert.Equal(ProposalState.Confirmed, (await Repository.GetProposalAsync(id, Ct))!.State);
    }

    [Fact]
    public async Task Confirming_changed_words_records_that_the_person_edited_them()
    {
        var id = await ProposedAsync();

        var edited = Content("Billing export", Item("Add CSV export", Billing), Item("Reword this", Orders, 1), Item("Document the column", null, 2));
        var change = await Repository.ConfirmAsync(id, edited, null, Now, cancellationToken: Ct);

        Assert.True(change.Done, change.Problem);
        Assert.Equal(RoadmapOrigin.Edited, change.Origin);
    }

    [Fact]
    public async Task Confirming_names_an_intended_agent_that_is_not_in_the_project_and_writes_nothing()
    {
        var id = await ProposedAsync();

        var stranger = Content("Billing export", Item("Add CSV export", AgentName.Create("nobody@test")));
        var change = await Repository.ConfirmAsync(id, stranger, null, Now, cancellationToken: Ct);

        Assert.False(change.Done);
        Assert.Contains("nobody@test", change.Problem, StringComparison.Ordinal);
        Assert.Equal(0L, _store.Count("SELECT count(*) FROM roadmaps;"));
        Assert.Equal(1L, _store.Count("SELECT count(*) FROM roadmap_proposals WHERE state = 'waiting';"));
        Assert.Equal(0L, _store.Count("SELECT count(*) FROM roadmap_notices;"));
    }

    [Fact]
    public async Task A_retired_agent_is_not_in_the_project_for_this_purpose()
    {
        var id = await ProposedAsync();
        _store.Execute("UPDATE agents SET status = 'retired' WHERE name = 'billing@test';");

        var proposal = await Repository.GetProposalAsync(id, Ct);
        var change = await Repository.ConfirmAsync(id, proposal!.Content, null, Now, cancellationToken: Ct);

        Assert.False(change.Done);
    }

    [Fact]
    public async Task A_decided_proposal_cannot_be_decided_again()
    {
        var id = await ProposedAsync();
        var proposal = await Repository.GetProposalAsync(id, Ct);
        await Repository.ConfirmAsync(id, proposal!.Content, null, Now, cancellationToken: Ct);

        var again = await Repository.ConfirmAsync(id, proposal.Content, null, Now, cancellationToken: Ct);
        var declined = await Repository.DeclineAsync(id, Now, cancellationToken: Ct);

        Assert.False(again.Done);
        Assert.False(declined.Done);
        Assert.Equal(1L, _store.Count("SELECT count(*) FROM roadmaps;"));
    }

    [Fact]
    public async Task Confirming_tells_the_proposer_in_its_own_title_and_nobody_else()
    {
        var id = await ProposedAsync("My own title");
        var proposal = await Repository.GetProposalAsync(id, Ct);
        await Repository.ConfirmAsync(id, proposal!.Content, null, Now, cancellationToken: Ct);

        var told = await Repository.DrainNoticesAsync(Orders, 10, Now, cancellationToken: Ct);
        var other = await Repository.DrainNoticesAsync(Billing, 10, Now, cancellationToken: Ct);

        var notice = Assert.Single(told);
        Assert.Equal("My own title", notice.Title);
        Assert.Equal(ProposalState.Confirmed, notice.Outcome);
        Assert.Empty(other);
    }

    [Fact]
    public async Task A_notice_is_collected_once_and_wakes_nothing_because_it_is_only_counted()
    {
        var id = await ProposedAsync();
        await Repository.DeclineAsync(id, Now, cancellationToken: Ct);

        Assert.Equal(1, await Repository.CountNoticesAsync(Orders, Ct));
        Assert.Single(await Repository.DrainNoticesAsync(Orders, 10, Now, cancellationToken: Ct));
        Assert.Equal(0, await Repository.CountNoticesAsync(Orders, Ct));
        Assert.Empty(await Repository.DrainNoticesAsync(Orders, 10, Now, cancellationToken: Ct));
    }

    [Fact]
    public async Task Declining_persists_no_roadmap_and_tells_the_proposer()
    {
        var id = await ProposedAsync();

        var change = await Repository.DeclineAsync(id, Now, cancellationToken: Ct);

        Assert.True(change.Done);
        Assert.Equal(0L, _store.Count("SELECT count(*) FROM roadmaps;"));
        Assert.Equal(ProposalState.Declined, (await Repository.GetProposalAsync(id, Ct))!.State);

        var notice = Assert.Single(await Repository.DrainNoticesAsync(Orders, 10, Now, cancellationToken: Ct));
        Assert.Equal(ProposalState.Declined, notice.Outcome);
    }

    [Fact]
    public async Task Confirming_a_revision_supersedes_the_other_waiting_proposal_that_hints_at_the_same_roadmap()
    {
        var first = await ProposedAsync("Billing export");
        var firstProposal = await Repository.GetProposalAsync(first, Ct);
        var created = await Repository.ConfirmAsync(first, firstProposal!.Content, null, Now, cancellationToken: Ct);

        var one = await ProposedAsync("Billing export v2", Orders, target: "Billing export");
        var two = await ProposedAsync("Billing export, again", Billing, target: "BILLING EXPORT");
        var unrelated = await ProposedAsync("Something else", Billing, target: "Other");

        var revision = await Repository.ConfirmAsync(
            one,
            Content("Billing export", Item("Only item", Billing)),
            created.RoadmapId,
            Now,
            cancellationToken: Ct);

        Assert.True(revision.Done, revision.Problem);
        Assert.Equal(2, revision.Revision);
        Assert.Equal(ProposalState.Superseded, (await Repository.GetProposalAsync(two, Ct))!.State);
        Assert.Equal(ProposalState.Waiting, (await Repository.GetProposalAsync(unrelated, Ct))!.State);

        var told = await Repository.DrainNoticesAsync(Billing, 10, Now, cancellationToken: Ct);
        Assert.Contains(told, n => n.ProposalId == two && n.Outcome == ProposalState.Superseded);
    }

    [Fact]
    public async Task Confirming_a_new_roadmap_under_a_taken_title_leaves_a_revision_of_the_existing_one_waiting()
    {
        // A proposer chooses its own title, so matching the confirmed title would let one agent's proposal decide
        // another's, with no decision by the person on it. A new roadmap revises nothing.
        var first = await ProposedAsync("Billing export");
        await Repository.ConfirmAsync(first, (await Repository.GetProposalAsync(first, Ct))!.Content, null, Now, cancellationToken: Ct);

        var revision = await ProposedAsync("Billing export, more", Billing, target: "Billing export");
        var rival = await ProposedAsync("Billing export");

        var created = await Repository.ConfirmAsync(
            rival, (await Repository.GetProposalAsync(rival, Ct))!.Content, null, Now, cancellationToken: Ct);

        Assert.True(created.Done, created.Problem);
        Assert.Equal(ProposalState.Waiting, (await Repository.GetProposalAsync(revision, Ct))!.State);
    }

    [Fact]
    public async Task A_revision_supersedes_the_hints_at_its_roadmaps_title_before_the_change_and_no_other()
    {
        var launch = await ProposedAsync("Launch");
        var one = await Repository.ConfirmAsync(launch, (await Repository.GetProposalAsync(launch, Ct))!.Content, null, Now, cancellationToken: Ct);
        var billing = await ProposedAsync("Billing export");
        await Repository.ConfirmAsync(billing, (await Repository.GetProposalAsync(billing, Ct))!.Content, null, Now, cancellationToken: Ct);

        var atOne = await ProposedAsync("Launch, sooner", Billing, target: "Launch");
        var atTwo = await ProposedAsync("Billing export, more", Billing, target: "Billing export");
        var renaming = await ProposedAsync("Billing export", Orders, target: "Launch");

        var revised = await Repository.ConfirmAsync(
            renaming, Content("Billing export", Item("Only item", Billing)), one.RoadmapId, Now, cancellationToken: Ct);

        Assert.True(revised.Done, revised.Problem);
        Assert.Equal(ProposalState.Superseded, (await Repository.GetProposalAsync(atOne, Ct))!.State);
        Assert.Equal(ProposalState.Waiting, (await Repository.GetProposalAsync(atTwo, Ct))!.State);
    }

    [Fact]
    public async Task A_revision_supersedes_a_hint_the_surfaces_show_as_naming_it_whatever_the_case_of_its_letters()
    {
        // The surfaces show a proposal as aimed at a roadmap when its hint equals the title ignoring case
        // (ProposalText.Matching, OrdinalIgnoreCase). The supersede must agree, or a proposal shown as aimed at the
        // roadmap the person just revised is left waiting.
        var cafe = await ProposedAsync("Café menu");
        var one = await Repository.ConfirmAsync(cafe, (await Repository.GetProposalAsync(cafe, Ct))!.Content, null, Now, cancellationToken: Ct);

        var shouting = await ProposedAsync("Menu, sooner", Billing, target: "CAFÉ MENU");
        var reviser = await ProposedAsync("Café menu", Orders, target: "Café menu");

        Assert.Equal("CAFÉ MENU", "Café menu", ignoreCase: true);

        var revised = await Repository.ConfirmAsync(
            reviser, Content("Café menu", Item("Only item", Billing)), one.RoadmapId, Now, cancellationToken: Ct);

        Assert.True(revised.Done, revised.Problem);
        Assert.Equal(ProposalState.Superseded, (await Repository.GetProposalAsync(shouting, Ct))!.State);
    }

    [Fact]
    public async Task A_revision_does_not_read_every_waiting_hint_whole_to_find_the_ones_it_supersedes()
    {
        // A hint has no bound and an agent may make as many as it likes. Reading every waiting proposal whole inside the
        // confirm's write held hundreds of megabytes to supersede nothing; only hints as long as the title are read now.
        var launch = await ProposedAsync("Launch");
        var one = await Repository.ConfirmAsync(launch, (await Repository.GetProposalAsync(launch, Ct))!.Content, null, Now, cancellationToken: Ct);
        var reviser = await ProposedAsync("Launch", Orders, target: "Launch");

        _store.Execute($$"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 6000)
            INSERT INTO roadmap_proposals (proposer, target_title, title, size, created_utc)
            SELECT 'billing@test', printf('%.*c', 10000, 'x'), printf('%.*c', 10000, 'y'), 20000, '{{FacilitatorStore.Stamp}}'
              FROM n;
            """);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var revised = await Repository.ConfirmAsync(
            reviser, Content("Launch", Item("Only item", Billing)), one.RoadmapId, Now, cancellationToken: Ct);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.True(revised.Done, revised.Problem);

        // Whole, those 6,000 proposals are 240 MB of strings; their hints alone, 120 MB.
        Assert.True(allocated < 40_000_000, $"a revision allocated {allocated:N0} bytes");
    }

    [Fact]
    public async Task A_rename_then_a_revision_decide_each_waiting_hint_once_by_the_title_before_the_revision()
    {
        var launch = await ProposedAsync("Launch");
        var one = await Repository.ConfirmAsync(launch, (await Repository.GetProposalAsync(launch, Ct))!.Content, null, Now, cancellationToken: Ct);

        var atOld = await ProposedAsync("Launch, sooner", Billing, target: "Launch");
        var atNew = await ProposedAsync("Ship, sooner", Billing, target: "Ship");
        var reviser = await ProposedAsync("Ship", Orders, target: "Launch");

        // The person renames it first, then confirms a revision against it: the title before the revision is "Ship".
        var renamed = await Repository.EditAsync(one.RoadmapId!.Value, 1, Content("Ship", Item("Only item", Billing)), Now, cancellationToken: Ct);
        Assert.True(renamed.Done, renamed.Problem);

        var revised = await Repository.ConfirmAsync(
            reviser, Content("Ship", Item("Only item", Billing), Item("Another", Orders)), one.RoadmapId, Now, cancellationToken: Ct);
        Assert.True(revised.Done, revised.Problem);

        Assert.Equal(ProposalState.Superseded, (await Repository.GetProposalAsync(atNew, Ct))!.State);
        Assert.Equal(ProposalState.Waiting, (await Repository.GetProposalAsync(atOld, Ct))!.State);

        // A rename made against the revision before the confirm is refused, not applied over it.
        var late = await Repository.EditAsync(one.RoadmapId!.Value, 2, Content("Later", Item("Only item", Billing)), Now, cancellationToken: Ct);
        Assert.False(late.Done);

        var superseded = (await Repository.ListProposalsAsync(ProposalState.Superseded, Ct)).Count;
        Assert.Equal(1, superseded);
    }

    // ---- editing

    private async Task<long> RoadmapAsync()
    {
        var id = await ProposedAsync();
        var proposal = await Repository.GetProposalAsync(id, Ct);
        var change = await Repository.ConfirmAsync(id, proposal!.Content, null, Now, cancellationToken: Ct);

        return change.RoadmapId!.Value;
    }

    private static RoadmapItemDraft Existing(int number, string text, AgentName? agent = null, params int[] needs) =>
        new(number, text, agent, needs);

    [Fact]
    public async Task An_edit_from_a_stale_revision_is_refused()
    {
        var id = await RoadmapAsync();
        var roadmap = await Repository.GetAsync(id, Ct);
        var content = Content("Billing export", [.. roadmap!.Items.Select(i => Existing(i.Number, i.Text))]);

        var first = await Repository.EditAsync(id, 1, content, Now, cancellationToken: Ct);
        var stale = await Repository.EditAsync(id, 1, content, Now, cancellationToken: Ct);

        Assert.True(first.Done, first.Problem);
        Assert.False(stale.Done);
        Assert.Contains("revision 2", stale.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_items_can_swap_places_though_the_index_forbids_two_in_one()
    {
        var id = await ProposedAsync();
        var proposal = await Repository.GetProposalAsync(id, Ct);
        var plain = Content("Plan", Item("First"), Item("Second"), Item("Third"));
        var made = await Repository.ConfirmAsync(id, plain, null, Now, cancellationToken: Ct);
        _ = proposal;

        var swapped = Content("Plan", Existing(2, "Second"), Existing(1, "First"), Existing(3, "Third"));
        var edit = await Repository.EditAsync(made.RoadmapId!.Value, 1, swapped, Now, cancellationToken: Ct);

        Assert.True(edit.Done, edit.Problem);

        var roadmap = await Repository.GetAsync(made.RoadmapId.Value, Ct);
        Assert.Equal(["Second", "First", "Third"], roadmap!.Items.Select(i => i.Text));
        Assert.Equal([2, 1, 3], roadmap.Items.Select(i => i.Number));
    }

    [Fact]
    public async Task A_reorder_that_leaves_an_item_needing_a_later_one_is_refused_and_changes_nothing()
    {
        var id = await RoadmapAsync();
        var before = await Repository.GetAsync(id, Ct);

        // Item 2 needed item 1. Moved above it, the list says it needs something later than itself.
        var broken = Content("Billing export", Existing(2, "Call it from the report", Orders, 2), Existing(1, "Add CSV export", Billing), Existing(3, "Document the column"));

        var edit = await Repository.EditAsync(id, 1, broken, Now, cancellationToken: Ct);

        Assert.False(edit.Done);

        var after = await Repository.GetAsync(id, Ct);
        Assert.Equal(before!.Revision, after!.Revision);
        Assert.Equal(before.Items.Select(i => i.Text), after.Items.Select(i => i.Text));
    }

    [Fact]
    public async Task Removing_an_item_keeps_its_row_and_its_reservation_and_a_new_one_gets_a_new_number()
    {
        var id = await RoadmapAsync();

        var edited = Content(
            "Billing export",
            Existing(1, "Add CSV export", Billing),
            Existing(3, "Document the column", null, 1),
            Item("A new item"));

        var edit = await Repository.EditAsync(id, 1, edited, Now, cancellationToken: Ct);

        Assert.True(edit.Done, edit.Problem);

        var roadmap = await Repository.GetAsync(id, Ct);
        var removed = Assert.Single(roadmap!.Items, i => i.Removed);

        Assert.Equal(2, removed.Number);
        Assert.Equal(2, removed.RemovedRevision);
        Assert.Equal("rm1-2", removed.ConversationName);
        Assert.Equal(1L, _store.Count("SELECT count(*) FROM roadmap_item_conversations WHERE conversation_id = 'rm1-2';"));

        // Numbers are never reused, so the new item cannot take the conversation the removed one reserved.
        var added = roadmap.Items.Single(i => i.Text == "A new item");
        Assert.Equal(4, added.Number);
        Assert.Equal("rm1-4", added.ConversationName);

        Assert.DoesNotContain(roadmap.Items.Where(i => !i.Removed), i => i.Needs.Contains(2));
    }

    [Fact]
    public async Task An_edit_cannot_name_an_item_that_is_not_live_in_the_roadmap()
    {
        var id = await RoadmapAsync();

        var edit = await Repository.EditAsync(
            id, 1, Content("Billing export", Existing(99, "Nothing")), Now, cancellationToken: Ct);

        Assert.False(edit.Done);
    }

    [Fact]
    public async Task Rewording_an_item_marks_it_edited_and_leaving_it_alone_does_not()
    {
        var id = await RoadmapAsync();

        var edit = await Repository.EditAsync(
            id,
            1,
            Content("Billing export", Existing(1, "Add CSV export, now with headers", Billing), Existing(2, "Call it from the report", Orders, 1), Existing(3, "Document the column", null, 2)),
            Now,
            cancellationToken: Ct);

        Assert.True(edit.Done, edit.Problem);
        Assert.Equal(1L, _store.Count("SELECT count(*) FROM roadmap_items WHERE edited_utc IS NOT NULL;"));
    }

    // ---- storage rules, reached without the repository

    [Fact]
    public async Task Two_live_items_cannot_share_a_place_but_a_removed_one_may_keep_its_old_one()
    {
        var id = await RoadmapAsync();

        Refused($"INSERT INTO roadmap_items (roadmap_id, number, position, text, added_rev) VALUES ({id}, 9, 1, 'x', 1);");

        _store.Execute($"INSERT INTO roadmap_items (roadmap_id, number, position, text, added_rev, removed_rev) VALUES ({id}, 9, 1, 'x', 1, 2);");
    }

    [Fact]
    public async Task An_item_is_not_removed_in_or_before_the_revision_that_added_it()
    {
        var id = await RoadmapAsync();

        Refused($"INSERT INTO roadmap_items (roadmap_id, number, position, text, added_rev, removed_rev) VALUES ({id}, 9, 50, 'x', 2, 2);");
    }

    [Fact]
    public async Task A_need_must_name_a_live_earlier_item_of_the_same_roadmap()
    {
        var id = await RoadmapAsync();
        var one = (long)_store.Scalar($"SELECT id FROM roadmap_items WHERE roadmap_id = {id} AND number = 1;")!;
        var three = (long)_store.Scalar($"SELECT id FROM roadmap_items WHERE roadmap_id = {id} AND number = 3;")!;

        // Item 1 needing item 3 points forward.
        var refused = Refused($"INSERT INTO roadmap_item_needs (item_id, needs_item_id) VALUES ({one}, {three});");
        Assert.Contains("earlier", refused.Message, StringComparison.Ordinal);

        Refused($"INSERT INTO roadmap_item_needs (item_id, needs_item_id) VALUES ({one}, {one});");
    }

    [Fact]
    public async Task A_roadmap_and_its_items_carry_no_status_of_any_kind()
    {
        await RoadmapAsync();

        foreach (var table in new[] { "roadmaps", "roadmap_items" })
        {
            using var command = _store.Connection.CreateCommand();
            command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var column = reader.GetString(0);
                Assert.DoesNotContain("status", column, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("state", column, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("done", column, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task A_purged_proposer_takes_its_proposals_and_notices_but_not_the_roadmap_they_became()
    {
        var id = await ProposedAsync();
        var proposal = await Repository.GetProposalAsync(id, Ct);
        await Repository.ConfirmAsync(id, proposal!.Content, null, Now, cancellationToken: Ct);
        await ProposedAsync("Second", Orders);

        _store.Execute("PRAGMA foreign_keys = ON; DELETE FROM agents WHERE name = 'orders@test';");

        Assert.Equal(0L, _store.Count("SELECT count(*) FROM roadmap_proposals;"));
        Assert.Equal(0L, _store.Count("SELECT count(*) FROM roadmap_notices;"));
        Assert.Equal(1L, _store.Count("SELECT count(*) FROM roadmaps;"));
        Assert.Equal("orders@test", (string)_store.Scalar("SELECT proposer FROM roadmaps;")!);
    }

    [Fact]
    public async Task Purging_an_items_agent_keeps_the_persons_item_and_its_conversation_name()
    {
        // Through the purge itself, not a raw DELETE: the purge swept every reference to the agent, the SET NULL ones
        // too, and so deleted the person's item, freed its reserved name and dropped the needs on it.
        var id = await ProposedAsync();
        var created = await Repository.ConfirmAsync(id, (await Repository.GetProposalAsync(id, Ct))!.Content, null, Now, cancellationToken: Ct);

        await new AgentRepository(_store.Connection).PurgeAsync(Billing, Now, cancellationToken: Ct);

        var read = (await Repository.GetAsync(created.RoadmapId!.Value, Ct))!;
        Assert.Equal(3, read.Items.Count);
        Assert.Null(read.Items[0].IntendedAgent);
        Assert.Equal(1L, _store.Count("SELECT count(*) FROM roadmap_item_conversations WHERE conversation_id = 'rm1-1';"));
    }

    [Fact]
    public async Task Purging_an_items_agent_keeps_another_agents_waiting_proposal_whole()
    {
        var id = await ProposedAsync();

        await new AgentRepository(_store.Connection).PurgeAsync(Billing, Now, cancellationToken: Ct);

        var read = (await Repository.GetProposalAsync(id, Ct))!;
        Assert.Equal(["Add CSV export", "Call it from the report", "Document the column"], read.Content.Items.Select(i => i.Text));
        Assert.Null(read.Content.Items[0].IntendedAgent);
    }

    // ---- an item's conversation is opened by the person

    private void Open(string conversation, string from, string to, string model = "claude-opus-5")
    {
        var pair = string.CompareOrdinal(from, to) < 0 ? from + (char)31 + to : to + (char)31 + from;

        _store.Execute($$"""
            INSERT INTO conversations (id, state, opened_utc) VALUES ('{{conversation}}', 'OPEN', '{{FacilitatorStore.Stamp}}')
              ON CONFLICT DO NOTHING;
            INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
            VALUES ('{{conversation}}', '{{from}}', '{{FacilitatorStore.Stamp}}'),
                   ('{{conversation}}', '{{to}}', '{{FacilitatorStore.Stamp}}')
              ON CONFLICT DO NOTHING;
            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 uncollected_flag_ms, stalled_flag_ms, request_hash)
            VALUES ('{{pair}}', '{{from}}', '{{to}}', '{{to}}', 'OPEN', '{{conversation}}',
                    '{{FacilitatorStore.Stamp}}', 900000, 14400000, 'hash-{{Guid.NewGuid():N}}');
            INSERT INTO messages
                (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                 content_hash, conversation_id, created_utc)
            VALUES (last_insert_rowid(), 'request', '{{from}}', '{{to}}', '{{model}}',
                    '{"ask":"Add CSV export"}', '{{Guid.NewGuid():N}}{{Guid.NewGuid():N}}', '{{conversation}}', '{{FacilitatorStore.Stamp}}');
            """);
    }

    [Fact]
    public async Task An_agent_cannot_open_a_conversation_the_roadmap_reserved()
    {
        await RoadmapAsync();

        var refused = Assert.Throws<SqliteException>(() => Open("rm1-1", "orders@test", "billing@test"));

        Assert.Contains("opened by the person", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0L, _store.Count("SELECT count(*) FROM messages WHERE conversation_id = 'rm1-1';"));
    }

    [Fact]
    public async Task The_trigger_is_what_refuses_it_because_without_it_the_same_open_is_admitted()
    {
        // Proof the test above is not passing for a reason of the fixture: drop the rule and the identical
        // open by an agent succeeds, which is the capture the rule exists to stop.
        await RoadmapAsync();
        _store.Execute("DROP TRIGGER tr_a_reserved_conversation_opens_with_the_person;");

        Open("rm1-1", "orders@test", "billing@test");

        Assert.Equal(1L, _store.Count("SELECT count(*) FROM messages WHERE conversation_id = 'rm1-1';"));
    }

    [Fact]
    public async Task The_person_may_open_it_and_everyone_may_then_reply_in_it()
    {
        await RoadmapAsync();

        Open("rm1-1", "human-user@test", "orders@test");

        // A second exchange in the same conversation, from an agent: the rule holds the first message only.
        Open("rm1-1", "orders@test", "billing@test");

        Assert.Equal(2L, _store.Count("SELECT count(*) FROM messages WHERE conversation_id = 'rm1-1';"));
    }

    [Fact]
    public async Task A_conversation_no_item_reserved_is_untouched_by_the_rule()
    {
        await RoadmapAsync();

        Open("rm9-9", "orders@test", "billing@test");
        Open("some-other-work", "orders@test", "billing@test");

        Assert.Equal(2L, _store.Count("SELECT count(*) FROM messages;"));
    }

    [Fact]
    public async Task A_conversation_is_recognised_as_an_items_by_the_reservation_and_never_by_its_name()
    {
        var id = await RoadmapAsync();

        // Same shape, no reservation: an agent that names a conversation rm5-1 has made nothing of the sort.
        Open("rm5-1", "orders@test", "billing@test");

        Assert.Null(await Repository.ReservationOfAsync("rm5-1", Ct));

        var reserved = await Repository.ReservationOfAsync("rm1-2", Ct);
        Assert.NotNull(reserved);
        Assert.Equal(id, reserved.Value.RoadmapId);
        Assert.Equal(2, reserved.Value.ItemNumber);
        Assert.False(reserved.Value.Removed);
    }

    [Fact]
    public async Task The_facts_say_whether_work_started_and_whether_the_request_was_collected()
    {
        var id = await RoadmapAsync();

        var before = (await Repository.GetAsync(id, Ct))!.Items[0].Facts!;
        Assert.False(before.ConversationExists);
        Assert.Equal(0, before.OpenExchanges);

        Open("rm1-1", "human-user@test", "billing@test");

        var started = (await Repository.GetAsync(id, Ct))!.Items[0].Facts!;
        Assert.True(started.ConversationExists);
        Assert.Equal(1, started.OpenExchanges);
        Assert.False(started.Collected);
        Assert.False(started.TextEditedSinceStarted);

        _store.Execute($"UPDATE exchanges SET delivered_utc = '{FacilitatorStore.Stamp}' WHERE conversation_id = 'rm1-1';");

        Assert.True((await Repository.GetAsync(id, Ct))!.Items[0].Facts!.Collected);
        Assert.Equal(1, (await Repository.ListAsync(Ct)).Single().StartedItems);
    }

    [Fact]
    public async Task An_item_whose_name_an_agent_took_first_is_reserved_under_a_free_name_and_has_not_begun()
    {
        // The next item's name is predictable, so an agent opens rm1-4 before the person adds item 4. Must fail against
        // reserving the agent's conversation for the item, which would read as begun with the agent's words in it.
        var id = await RoadmapAsync();
        Open("rm1-4", "orders@test", "billing@test");

        var edit = await Repository.EditAsync(
            id,
            1,
            Content("Billing export",
                Existing(1, "Add CSV export", Billing),
                Existing(2, "Call it from the report", Orders, 1),
                Existing(3, "Document the column", null, 2),
                Item("Ship it", Billing)),
            Now,
            cancellationToken: Ct);

        Assert.True(edit.Done, edit.Problem);

        var added = (await Repository.GetAsync(id, Ct))!.Items.Single(i => i.Text == "Ship it");

        Assert.Equal("rm1-4-2", added.ConversationName);
        Assert.False(added.Facts!.ConversationExists);
        Assert.Null(await Repository.ReservationOfAsync("rm1-4", Ct));
    }

    [Fact]
    public async Task New_items_whose_names_are_taken_get_unique_names_that_last_through_later_edits()
    {
        // Two derived names taken by an agent, three items added in one edit, then a reorder and another item. Must fail
        // against two items sharing a name, or a later edit moving an item's conversation.
        var id = await RoadmapAsync();
        Open("rm1-4", "orders@test", "billing@test");
        Open("rm1-5", "orders@test", "billing@test");

        var edit = await Repository.EditAsync(
            id,
            1,
            Content("Billing export",
                Existing(1, "Add CSV export", Billing),
                Existing(2, "Call it from the report", Orders, 1),
                Existing(3, "Document the column", null, 2),
                Item("Ship it", Billing),
                Item("Tell them", Billing),
                Item("Close it", Billing)),
            Now,
            cancellationToken: Ct);

        Assert.True(edit.Done, edit.Problem);

        var items = (await Repository.GetAsync(id, Ct))!.Items;
        Assert.Equal(items.Count, items.Select(i => i.ConversationName).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("rm1-4-2", items.Single(i => i.Text == "Ship it").ConversationName);
        Assert.Equal("rm1-5-2", items.Single(i => i.Text == "Tell them").ConversationName);
        Assert.Equal("rm1-6", items.Single(i => i.Text == "Close it").ConversationName);

        var again = await Repository.EditAsync(
            id,
            2,
            Content("Billing export",
                Existing(1, "Add CSV export", Billing),
                Existing(2, "Call it from the report", Orders, 1),
                Existing(3, "Document the column", null, 2),
                Existing(5, "Tell them", Billing),
                Existing(4, "Ship it", Billing),
                Existing(6, "Close it", Billing),
                Item("Seven", Billing)),
            Now,
            cancellationToken: Ct);

        Assert.True(again.Done, again.Problem);

        var after = (await Repository.GetAsync(id, Ct))!.Items;
        Assert.Equal("rm1-4-2", after.Single(i => i.Number == 4).ConversationName);
        Assert.Equal("rm1-5-2", after.Single(i => i.Number == 5).ConversationName);
        Assert.Equal("rm1-7", after.Single(i => i.Number == 7).ConversationName);
        Assert.Equal(4, (await Repository.ReservationOfAsync("rm1-4-2", Ct))!.Value.ItemNumber);
    }

    [Fact]
    public async Task An_item_reworded_after_its_request_was_sent_says_so_and_one_reworded_before_does_not()
    {
        // FR-014a, from the item's own edit record: the words sent may be the person's own and prove nothing. Item 1 is
        // started and then reworded; item 2 is reworded and never started. Must fail against a fact that ignores the
        // edit, and against one that marks an item reworded before anything was sent.
        var id = await RoadmapAsync();
        Open("rm1-1", "human-user@test", "billing@test");

        var later = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var edit = await Repository.EditAsync(
            id,
            1,
            Content(
                "Billing export",
                Existing(1, "Add CSV export with headers", Billing),
                Existing(2, "Call it from the monthly report", Orders, 1),
                Existing(3, "Document the column", null, 2)),
            later,
            cancellationToken: Ct);

        Assert.True(edit.Done, edit.Problem);

        var items = (await Repository.GetAsync(id, Ct))!.Items;
        Assert.True(items[0].Facts!.TextEditedSinceStarted);
        Assert.False(items[1].Facts!.TextEditedSinceStarted);
    }

    /// <summary>Ends the newest open exchange of a conversation on a Result from its receiver, as an agent's answer does.</summary>
    private void Answer(string conversation, string outcome)
    {
        var item = outcome == "blocked"
            ? """{"outcomes":[{"id":"q1","outcome":"blocked","text":"no","blocker":"permission"}]}"""
            : """{"outcomes":[{"id":"q1","outcome":"done","text":"ok"}]}""";

        _store.Execute($$"""
            INSERT INTO messages
                (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                 content_hash, conversation_id, created_utc)
            SELECT id, 'result', receiver, requester, 'claude-opus-5', '{{item}}',
                   '{{Guid.NewGuid():N}}{{Guid.NewGuid():N}}', conversation_id, '{{FacilitatorStore.Stamp}}'
              FROM exchanges WHERE conversation_id = '{{conversation}}' AND state = 'OPEN' ORDER BY id DESC LIMIT 1;
            UPDATE exchanges SET state = 'CLOSED', closed_utc = '{{FacilitatorStore.Stamp}}', outcome = '{{outcome}}'
             WHERE id = (SELECT MAX(id) FROM exchanges WHERE conversation_id = '{{conversation}}' AND state = 'OPEN');
            """);
    }

    [Fact]
    public async Task A_blocker_the_agent_has_since_answered_past_no_longer_blocks_the_item()
    {
        var id = await RoadmapAsync();

        Open("rm1-1", "human-user@test", "billing@test");
        Answer("rm1-1", "blocked");
        Assert.Equal(1, (await Repository.GetAsync(id, Ct))!.Items[0].Facts!.BlockedExchanges);

        // The person resolved it and asked again; the agent finished. The blocked exchange is still in the record.
        Open("rm1-1", "human-user@test", "billing@test");
        Answer("rm1-1", "complete");

        var facts = (await Repository.GetAsync(id, Ct))!.Items[0].Facts!;
        Assert.Equal(0, facts.BlockedExchanges);
        Assert.Equal(1, facts.CompleteExchanges);
    }

    [Fact]
    public async Task Retention_removes_old_decided_proposals_and_delivered_notices_and_never_a_roadmap_or_a_waiting_one()
    {
        // Three proposals: one confirmed long ago, one declined long ago, one still waiting and just as old.
        var confirmed = await ProposedAsync("Became a roadmap");
        var proposal = await Repository.GetProposalAsync(confirmed, Ct);
        var roadmap = await Repository.ConfirmAsync(confirmed, proposal!.Content, null, Now, cancellationToken: Ct);
        var declined = await ProposedAsync("Declined");
        await Repository.DeclineAsync(declined, Now, cancellationToken: Ct);
        var waiting = await ProposedAsync("Still waiting");

        await Repository.DrainNoticesAsync(Orders, 10, Now, cancellationToken: Ct);

        var later = Now.AddDays(400);
        var result = await new Pruner(_store.Connection).PruneAsync(RetentionPolicy.Default, later, Ct);

        Assert.NotNull(result);
        Assert.Null(await Repository.GetProposalAsync(confirmed, Ct));
        Assert.Null(await Repository.GetProposalAsync(declined, Ct));
        Assert.Equal(ProposalState.Waiting, (await Repository.GetProposalAsync(waiting, Ct))!.State);
        Assert.Equal(0L, _store.Count("SELECT count(*) FROM roadmap_notices WHERE delivered_utc IS NOT NULL;"));

        // Nothing about the roadmap went, and it still says who it began with.
        var kept = await Repository.GetAsync(roadmap.RoadmapId!.Value, Ct);
        Assert.NotNull(kept);
        Assert.Equal(3, kept.Items.Count);
        Assert.Equal("orders@test", kept.Proposer);
    }

    [Fact]
    public async Task The_conversation_list_marks_an_items_conversation_and_a_removed_ones_as_formerly()
    {
        var id = await RoadmapAsync();

        Open("rm1-1", "human-user@test", "billing@test");
        Open("rm1-3", "human-user@test", "orders@test");
        Open("plain-work", "orders@test", "billing@test");
        Open("rm7-7", "orders@test", "billing@test");

        var listed = (await new ConversationRepository(_store.Connection).ListAsync(cancellationToken: Ct))
            .ToDictionary(c => c.Id);

        Assert.Equal(1, listed["rm1-1"].Item!.ItemNumber);
        Assert.Equal("Billing export", listed["rm1-1"].Item!.Title);
        Assert.False(listed["rm1-1"].Item!.Removed);
        Assert.Null(listed["plain-work"].Item);

        // Same shape as an item's name, and reserved by nothing: it is not marked (SPEC.md 5.7).
        Assert.Null(listed["rm7-7"].Item);

        // Removing item 3 leaves its conversation, which then says it formerly belonged to the item.
        var edit = await Repository.EditAsync(
            id, 1, Content("Billing export", Existing(1, "Add CSV export", Billing), Existing(2, "Call it from the report", Orders, 1)),
            Now, cancellationToken: Ct);
        Assert.True(edit.Done, edit.Problem);

        var after = (await new ConversationRepository(_store.Connection).ListAsync(cancellationToken: Ct))
            .ToDictionary(c => c.Id);

        Assert.True(after["rm1-3"].Item!.Removed);
    }
}
