using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Every branch of <see cref="SharedFileRepository"/>: placing, replacing, resolving, removing, reconciling,
/// renaming, listing and counting (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The placer is the one rule that matters most here</b>, so every operation that changes a file is tried by the
/// agent that placed it, by another agent, and against a file the person put there. A check that only ever saw
/// its own agent would pass while letting any agent replace anybody's work.
/// </para>
/// <para>
/// The race for a new name runs on two real connections to one file, because that is where SQLite decides it: one
/// connection cannot race itself, and a test that simulated the loser would test the simulation.
/// </para>
/// </remarks>
public sealed class SharedFileRepositoryTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly SharedFileRepository _files;

    public SharedFileRepositoryTests()
    {
        _files = new SharedFileRepository(_connection);

        Work.Register(_connection, Orders, Billing);
        Conversation("pricing");
        Conversation("refunds");
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_name_is_placed_in_a_folder_named_for_its_conversation()
    {
        var result = await Place("pricing", "price-snapshot.md", "net of tax", Billing);

        Assert.Equal(PlaceOutcome.Placed, result.Outcome);
        Assert.Equal("pricing", result.File!.Folder);
        Assert.Equal(Billing, result.File.PlacedBy);
        Assert.Equal(ContentHash.Of("net of tax"u8), result.File.Sha256);
        Assert.Equal(10, result.File.SizeBytes);
    }

    [Fact]
    public async Task Its_placer_replacing_it_with_different_content_is_a_replacement_carrying_the_previous_hash()
    {
        var first = await Place("pricing", "price-snapshot.md", "net of tax", Billing);
        var second = await Place("pricing", "price-snapshot.md", "gross of tax", Billing, at: Now.AddHours(1));

        Assert.Equal(PlaceOutcome.Replaced, second.Outcome);
        Assert.Equal(first.File!.Sha256, second.Previous);
        Assert.Equal(ContentHash.Of("gross of tax"u8), second.File!.Sha256);
        Assert.Equal(first.File.PlacedUtc, second.File.PlacedUtc);
        Assert.Equal(Now.AddHours(1), second.File.ChangedUtc);
    }

    [Fact]
    public async Task Identical_content_changes_nothing_and_counts_nothing()
    {
        await Place("pricing", "price-snapshot.md", "net of tax", Billing);
        var again = await Place("pricing", "PRICE-SNAPSHOT.md", "net of tax", Billing, at: Now.AddHours(1));

        Assert.Equal(PlaceOutcome.AlreadyCurrent, again.Outcome);
        Assert.Equal(Now, again.File!.ChangedUtc);
        Assert.Equal(1, Assert.Single(await _files.GetPlacementsAsync(cancellationToken: Ct)).Placements);
    }

    [Fact]
    public async Task Another_agent_placing_that_name_is_refused_naming_the_placer_and_the_file_is_unchanged()
    {
        await Place("pricing", "price-snapshot.md", "net of tax", Billing);
        var refused = await Place("pricing", "Price-Snapshot.md", "something else", Orders);

        Assert.Equal(PlaceOutcome.PlacedByAnother, refused.Outcome);
        Assert.Equal(Billing, refused.File!.PlacedBy);
        Assert.Equal(ContentHash.Of("net of tax"u8), (await Get("pricing", "price-snapshot.md"))!.File.Sha256);
    }

    [Fact]
    public async Task A_file_the_person_put_there_cannot_be_replaced_or_removed_by_any_agent()
    {
        // A file can only appear in a folder that exists, and a conversation has a folder once it has had one.
        await _files.EnsureFolderAsync("pricing", _ => false, Now, cancellationToken: Ct);

        var found = await _files.RecordFoundAsync(
            "pricing", SharedFileName.Create("notes.md"), ContentHash.Of("a"u8), 1, 1, Now, cancellationToken: Ct);

        Assert.Null(found!.File.PlacedBy);
        Assert.Equal(PlaceOutcome.PlacedByAnother, (await Place("pricing", "notes.md", "b", Billing)).Outcome);
        Assert.Equal(FileRejection.NamePlacedByAnother, await _files.RemoveAsync(found, Billing, Now, cancellationToken: Ct));
        Assert.Equal(FileRejection.None, await _files.RemoveAsync(found, caller: null, Now, cancellationToken: Ct));
    }

    [Fact]
    public async Task The_same_name_in_two_conversations_is_two_independent_files()
    {
        await Place("pricing", "notes.md", "one", Billing);
        var other = await Place("refunds", "notes.md", "two", Orders);

        Assert.Equal(PlaceOutcome.Placed, other.Outcome);
        Assert.Equal(Orders, other.File!.PlacedBy);
    }

    [Fact]
    public async Task A_name_too_long_for_its_folder_is_refused_saying_how_long_a_name_the_folder_allows()
    {
        var name = new string('n', 248 - 3) + ".md";

        var result = await Place("pricing", name, "x", Billing);

        Assert.Equal(PlaceOutcome.DoesNotFit, result.Outcome);
        Assert.Equal(247, result.CharactersLeft);
    }

    [Fact]
    public async Task A_conversation_that_cannot_be_a_folder_as_it_is_gets_a_suffix_and_keeps_it()
    {
        Conversation("con");
        Conversation("Pricing");

        Assert.Equal("con~2", (await Place("con", "a.md", "x", Billing)).File!.Folder);

        await Place("pricing", "a.md", "x", Billing);
        Assert.Equal("Pricing~2", (await Place("Pricing", "a.md", "x", Billing)).File!.Folder);
    }

    [Fact]
    public async Task A_folder_already_on_disk_is_not_adopted()
    {
        var folder = await _files.EnsureFolderAsync("pricing", f => f == "pricing", Now, cancellationToken: Ct);

        Assert.Equal("pricing~2", folder);
        Assert.Equal("pricing", await _files.GetConversationForFolderAsync("PRICING~2", cancellationToken: Ct) is null
            ? "missing"
            : "pricing");
    }

    [Fact]
    public async Task A_folder_keeps_its_name_after_its_last_file_goes_and_after_a_prune()
    {
        Conversation("con");
        var placed = await Place("con", "a.md", "x", Billing);

        await _files.RemoveAsync((await Get("con", "a.md"))!, Billing, Now, cancellationToken: Ct);
        await Place("con", "b.md", "y", Billing);
        await _files.RemoveAllAsync(Now, cancellationToken: Ct);

        Assert.Equal("con~2", placed.File!.Folder);
        Assert.Equal("con~2", await _files.GetFolderAsync("con", cancellationToken: Ct));
    }

    [Fact]
    public async Task Two_connections_placing_one_new_name_at_once_have_exactly_one_winner()
    {
        var path = Path.Combine(Path.GetTempPath(), "parley-race-" + Guid.NewGuid().ToString("N") + ".db");

        try
        {
            using (var setup = ParleyDatabase.OpenAndMigrate(path))
            {
                Work.Register(setup, Orders, Billing);
                using var command = setup.CreateCommand();
                command.CommandText =
                    $"INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', '{ExchangeRow.FormatTimestamp(Now)}');";
                command.ExecuteNonQuery();
            }

            for (var round = 0; round < 10; round++)
            {
                using var one = ParleyDatabase.Open(path);
                using var two = ParleyDatabase.Open(path);

                var name = SharedFileName.Create($"race-{round}.md");

                var results = await Task.WhenAll(
                    Task.Run(() => new SharedFileRepository(one).PlaceAsync(
                        "pricing", name, ContentHash.Of("one"u8), 3, Orders, 1, Now, _ => false, cancellationToken: Ct), Ct),
                    Task.Run(() => new SharedFileRepository(two).PlaceAsync(
                        "pricing", name, ContentHash.Of("two"u8), 3, Billing, 1, Now, _ => false, cancellationToken: Ct), Ct));

                Assert.Single(results, r => r.Outcome == PlaceOutcome.Placed);
                Assert.Single(results, r => r.Outcome == PlaceOutcome.PlacedByAnother);
            }
        }
        finally
        {
            // This file's pool only: clearing every pool would dispose handles other test classes are using.
            using (var pooled = ParleyDatabase.Open(path))
            {
                SqliteConnection.ClearPool(pooled);
            }

            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Best effort.
                }
            }
        }
    }

    [Fact]
    public async Task A_bare_name_held_by_one_conversation_resolves_to_it()
    {
        await Place("refunds", "schema.md", "x", Orders);

        var resolved = await _files.ResolveAsync(new FileRef(null, "SCHEMA.md"), cancellationToken: Ct);

        Assert.Null(resolved.Refusal);
        Assert.Equal("refunds", resolved.File!.File.ConversationId);
    }

    [Fact]
    public async Task A_bare_name_held_by_several_conversations_is_refused_listing_them()
    {
        await Place("pricing", "schema.md", "x", Orders);
        await Place("refunds", "schema.md", "y", Orders);

        var resolved = await _files.ResolveAsync(new FileRef(null, "schema.md"), cancellationToken: Ct);

        Assert.Null(resolved.File);
        Assert.Equal(FileRejection.AmbiguousName, resolved.Refusal!.Rejection);
        Assert.Equal(["pricing", "refunds"], resolved.Refusal.Conversations);
        Assert.Equal(0, resolved.Refusal.ConversationsMore);
    }

    [Fact]
    public async Task An_ambiguity_names_at_most_two_hundred_conversations_and_counts_the_rest()
    {
        for (var i = 0; i < 203; i++)
        {
            var id = $"c{i:D3}";
            Conversation(id);
            await Place(id, "notes.md", "x", Orders);
        }

        var refusal = (await _files.ResolveAsync(new FileRef(null, "notes.md"), cancellationToken: Ct)).Refusal!;

        Assert.Equal(FileLimits.ConversationsPerRefusal, refusal.Conversations.Count);
        Assert.Equal(3, refusal.ConversationsMore);
    }

    [Theory]
    [InlineData(null, "absent.md", FileRejection.NoSuchFile)]
    [InlineData("pricing", "absent.md", FileRejection.NoSuchFile)]
    [InlineData(null, "run.bat", FileRejection.ExtensionNotAllowed)]
    [InlineData(null, "../up.md", FileRejection.BadName)]
    public async Task A_name_that_matches_nothing_or_breaks_a_rule_is_refused(string? conversation, string name, FileRejection rejection)
    {
        var resolved = await _files.ResolveAsync(new FileRef(conversation, name), cancellationToken: Ct);

        Assert.Equal(rejection, resolved.Refusal!.Rejection);
    }

    [Fact]
    public async Task Removing_takes_one_file_and_touches_no_other()
    {
        await Place("pricing", "a.md", "x", Billing);
        await Place("pricing", "b.md", "y", Billing);
        await Place("refunds", "a.md", "z", Billing);

        Assert.Equal(FileRejection.None, await _files.RemoveAsync((await Get("pricing", "a.md"))!, Billing, Now, cancellationToken: Ct));

        Assert.Null(await Get("pricing", "a.md"));
        Assert.NotNull(await Get("pricing", "b.md"));
        Assert.NotNull(await Get("refunds", "a.md"));
    }

    [Fact]
    public async Task An_agent_removing_a_file_another_placed_is_refused_and_the_person_is_not()
    {
        await Place("pricing", "a.md", "x", Billing);
        var file = (await Get("pricing", "a.md"))!;

        Assert.Equal(FileRejection.NamePlacedByAnother, await _files.RemoveAsync(file, Orders, Now, cancellationToken: Ct));
        Assert.NotNull(await Get("pricing", "a.md"));
        Assert.Equal(FileRejection.None, await _files.RemoveAsync(file, caller: null, Now, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_removal_of_a_file_already_gone_says_so()
    {
        await Place("pricing", "a.md", "x", Billing);
        var file = (await Get("pricing", "a.md"))!;

        await _files.RemoveAsync(file, Billing, Now, cancellationToken: Ct);

        Assert.Equal(FileRejection.NoSuchFile, await _files.RemoveAsync(file, Billing, Now, cancellationToken: Ct));
    }

    [Fact]
    public async Task Removing_everything_empties_the_location_and_nothing_else()
    {
        await Place("pricing", "a.md", "x", Billing);
        await Place("refunds", "b.md", "yy", Orders);

        var removed = await _files.RemoveAllAsync(Now, cancellationToken: Ct);

        Assert.Equal(2, removed.Count);
        Assert.Equal(3, removed.Sum(f => f.File.SizeBytes));
        Assert.Empty(await _files.GetAllAsync(cancellationToken: Ct));
        Assert.Equal(2L, Scalar("SELECT count(*) FROM conversations WHERE id IN ('pricing', 'refunds');"));
    }

    [Fact]
    public async Task A_reconcile_with_the_same_hash_changes_nothing_but_the_time_it_read()
    {
        await Place("pricing", "a.md", "x", Billing);
        var known = (await Get("pricing", "a.md"))!;

        Assert.False(await _files.ReconcileAsync(known, known.File.Sha256, 1, 999, Now.AddHours(1), cancellationToken: Ct));

        var after = (await Get("pricing", "a.md"))!;
        Assert.Equal(999, after.ModifiedTicks);
        Assert.Equal(Now, after.File.ChangedUtc);
    }

    [Fact]
    public async Task A_reconcile_with_a_new_hash_records_the_change_and_keeps_the_placer()
    {
        await Place("pricing", "a.md", "x", Billing);
        var known = (await Get("pricing", "a.md"))!;

        Assert.True(await _files.ReconcileAsync(known, ContentHash.Of("edited"u8), 6, 999, Now.AddHours(1), cancellationToken: Ct));

        var after = (await Get("pricing", "a.md"))!;
        Assert.Equal(ContentHash.Of("edited"u8), after.File.Sha256);
        Assert.Equal(6, after.File.SizeBytes);
        Assert.Equal(Billing, after.File.PlacedBy);
        Assert.Equal(Now.AddHours(1), after.File.ChangedUtc);
    }

    [Fact]
    public async Task A_reconcile_of_a_row_that_moved_on_since_it_was_read_writes_nothing()
    {
        // A look read the row and hashed the old bytes while a placement replaced them and recorded its own hash. Written,
        // the look's hash listed the old content over the new until the next look (macOS CI at f19f792).
        await Place("pricing", "a.md", "x", Billing);
        var known = (await Get("pricing", "a.md"))!;

        Assert.True(await _files.ReconcileAsync(known, ContentHash.Of("replaced"u8), 8, 500, Now.AddHours(1), cancellationToken: Ct));
        Assert.False(await _files.ReconcileAsync(known, ContentHash.Of("x"u8), 1, 400, Now.AddHours(2), cancellationToken: Ct));

        var after = (await Get("pricing", "a.md"))!;
        Assert.Equal(ContentHash.Of("replaced"u8), after.File.Sha256);
        Assert.Equal(500, after.ModifiedTicks);
    }

    [Fact]
    public async Task A_rename_keeps_the_placer_and_when_it_was_placed()
    {
        await Place("pricing", "a.md", "x", Billing);
        var known = (await Get("pricing", "a.md"))!;

        var renamed = await _files.RenameAsync(known, SharedFileName.Create("b.md"), 5, Now.AddHours(1), cancellationToken: Ct);

        Assert.Equal("b.md", renamed!.File.Name.Value);
        Assert.Equal(Billing, renamed.File.PlacedBy);
        Assert.Equal(Now, renamed.File.PlacedUtc);
        Assert.Null(await Get("pricing", "a.md"));
    }

    [Fact]
    public async Task A_rename_over_an_existing_name_takes_that_file_out_first()
    {
        await Place("pricing", "a.md", "x", Billing);
        await Place("pricing", "b.md", "y", Orders);

        var renamed = await _files.RenameAsync((await Get("pricing", "a.md"))!, SharedFileName.Create("b.md"), 5, Now, cancellationToken: Ct);

        Assert.Equal(Billing, renamed!.File.PlacedBy);
        Assert.Single(await _files.GetAllAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task A_list_is_paged_at_fifty_and_never_more_than_two_hundred_and_reaches_every_file_once()
    {
        for (var i = 0; i < 250; i++)
        {
            await Place(i % 2 == 0 ? "pricing" : "refunds", $"file-{i:D3}.md", $"content {i}", Billing);
        }

        var first = await _files.ListAsync(null, 0, null, cancellationToken: Ct);
        Assert.Equal(FileLimits.DefaultPageSize, first.Files.Count);
        Assert.Equal(250, first.TotalFiles);

        var big = await _files.ListAsync(null, 500, null, cancellationToken: Ct);
        Assert.Equal(FileLimits.MaximumPageSize, big.Files.Count);

        var seen = new List<string>();
        string? token = null;

        do
        {
            var page = await _files.ListAsync(null, 0, token, cancellationToken: Ct);

            Assert.Equal(250, page.TotalFiles);
            Assert.Equal(
                page.Files.Select(f => f.ConversationId).Distinct().Order(StringComparer.Ordinal),
                page.Totals.Select(t => t.ConversationId));
            Assert.All(page.Totals, t => Assert.Equal(125, t.Files));

            seen.AddRange(page.Files.Select(f => f.ConversationId + "/" + f.Name.Value));
            token = page.NextPageToken;
        }
        while (token is not null);

        Assert.Equal(250, seen.Count);
        Assert.Equal(250, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_file_removed_between_pages_does_not_make_another_appear_twice_or_go_missing()
    {
        for (var i = 0; i < 120; i++)
        {
            await Place("pricing", $"file-{i:D3}.md", $"content {i}", Billing);
        }

        var first = await _files.ListAsync(null, 50, null, cancellationToken: Ct);

        await _files.RemoveAsync((await Get("pricing", "file-010.md"))!, Billing, Now, cancellationToken: Ct);

        var second = await _files.ListAsync(null, 50, first.NextPageToken, cancellationToken: Ct);

        Assert.Equal("file-050.md", second.Files[0].Name.Value);
        Assert.Empty(first.Files.Select(f => f.Name.Value).Intersect(second.Files.Select(f => f.Name.Value)));
    }

    [Fact]
    public async Task A_list_for_one_conversation_holds_only_its_files_and_its_total()
    {
        await Place("pricing", "a.md", "x", Billing);
        await Place("refunds", "b.md", "yy", Billing);

        var page = await _files.ListAsync("refunds", 0, null, cancellationToken: Ct);

        Assert.Equal("b.md", Assert.Single(page.Files).Name.Value);
        Assert.Equal(1, page.TotalFiles);
        Assert.Equal(2, page.TotalBytes);
    }

    [Fact]
    public async Task A_page_token_the_store_did_not_issue_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _files.ListAsync(null, 0, "not-a-token!", cancellationToken: Ct));
    }

    [Fact]
    public async Task Placements_are_counted_per_agent_with_their_bytes_and_replacements_count_too()
    {
        await Place("pricing", "a.md", "12345", Billing);
        await Place("pricing", "a.md", "1234567", Billing);
        await Place("refunds", "b.md", "1", Orders);

        var counts = await _files.GetPlacementsAsync(cancellationToken: Ct);

        Assert.Equal(("billing@test", 2L, 12L), (counts[0].Agent, counts[0].Placements, counts[0].Bytes));
        Assert.Equal(("orders@test", 1L, 1L), (counts[1].Agent, counts[1].Placements, counts[1].Bytes));
    }

    [Fact]
    public async Task A_conversation_holding_only_files_is_listed_with_its_placer_and_no_messages()
    {
        // A listing that joined conversations to their messages would miss a conversation started by
        // placing a file: it exists, holds a file, has a participant, and must be found by the person.
        await Place("pricing", "a.md", "x", Billing);
        await new ConversationRepository(_connection).JoinByPlacingAsync("pricing", Billing, Now, cancellationToken: Ct);

        var listed = await new ConversationRepository(_connection).ListAsync(cancellationToken: Ct);

        var only = Assert.Single(listed, c => c.Id == "pricing");
        Assert.Equal(0, only.Messages);
        Assert.Equal([Billing], only.Participants);
        Assert.Equal(Now, only.LastActivityUtc);
    }

    [Fact]
    public async Task Destroying_a_conversation_takes_its_files_folder_and_fetch_records_and_reports_them()
    {
        await Place("pricing", "a.md", "x", Billing);
        await Place("refunds", "a.md", "x", Billing);
        await new FileNoticeRepository(_connection).RecordFetchAsync(
            Orders, "pricing", SharedFileName.Create("a.md"), ContentHash.Of("x"u8), Now, cancellationToken: Ct);

        var outcome = await new ConversationRepository(_connection).DestroyAsync("pricing", Now, cancellationToken: Ct);

        Assert.Null(await Get("pricing", "a.md"));
        Assert.NotNull(await Get("refunds", "a.md"));
        Assert.Null(await _files.GetFolderAsync("pricing", cancellationToken: Ct));
        Assert.Contains(outcome.Destroyed, t => t.Table == "shared_files" && t.Rows == 1);
        Assert.Contains(outcome.Destroyed, t => t.Table == "file_fetches" && t.Rows == 1);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task Purging_an_agent_takes_the_files_of_every_conversation_it_took_part_in()
    {
        await Place("pricing", "a.md", "x", Billing);
        await new ConversationRepository(_connection).JoinByPlacingAsync("pricing", Billing, Now, cancellationToken: Ct);
        await Place("refunds", "b.md", "y", Orders);
        await new ConversationRepository(_connection).JoinByPlacingAsync("refunds", Orders, Now, cancellationToken: Ct);

        var outcome = await new AgentRepository(_connection).PurgeAsync(Billing, Now, cancellationToken: Ct);

        Assert.Null(await Get("pricing", "a.md"));
        Assert.NotNull(await Get("refunds", "b.md"));
        Assert.Contains(outcome.Destroyed, t => t.Table == "shared_files" && t.Rows == 1);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    private Task<PlaceResult> Place(string conversation, string name, string content, AgentName by, DateTimeOffset? at = null) =>
        _files.PlaceAsync(
            conversation,
            SharedFileName.Create(name),
            ContentHash.Of(System.Text.Encoding.UTF8.GetBytes(content)),
            System.Text.Encoding.UTF8.GetByteCount(content),
            by,
            modifiedTicks: 1,
            at ?? Now,
            takenOnDisk: _ => false,
            cancellationToken: Ct);

    private Task<StoredFile?> Get(string conversation, string name) =>
        _files.GetAsync(conversation, SharedFileName.Create(name), cancellationToken: Ct);

    private void Conversation(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO conversations (id, state, opened_utc) VALUES ($id, 'OPEN', $when);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$when", ExchangeRow.FormatTimestamp(Now));
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
