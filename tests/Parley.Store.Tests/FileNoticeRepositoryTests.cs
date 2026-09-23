using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Change notices: who is told, what they are told, and when a notice stops waiting (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Each case is a way an agent could end up building on a stale copy without knowing</b> — a change nobody
/// raised, a second change that lost the first, a notice about content the agent has since fetched again — or
/// the opposite failure of being told something that is not true.
/// </para>
/// <para>
/// Retired agents appear on purpose. The owner's rule is that active agents are told, and a retired agent that
/// kept collecting notices would be the one place its retirement did not reach.
/// </para>
/// </remarks>
public sealed class FileNoticeRepositoryTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly SharedFileRepository _files;
    private readonly FileNoticeRepository _notices;

    public FileNoticeRepositoryTests()
    {
        _files = new SharedFileRepository(_connection);
        _notices = new FileNoticeRepository(_connection);

        Work.Register(_connection, Orders, Billing, Inventory);

        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', $when);";
        command.Parameters.AddWithValue("$when", ExchangeRow.FormatTimestamp(Now));
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_agent_that_fetched_a_file_is_told_when_it_changes_with_what_it_had_and_what_is_there_now()
    {
        await Place("net of tax");
        await Fetch(Orders, "net of tax");
        await Fetch(Inventory, "net of tax");

        await Place("gross of tax", Now.AddHours(1));

        foreach (var agent in new[] { Orders, Inventory })
        {
            var notice = Assert.Single((await Collect(agent)).Notices);

            Assert.Equal(ContentHash.Of("net of tax"u8), notice.Had);
            Assert.Equal(ContentHash.Of("gross of tax"u8), notice.Now);
            Assert.Equal(12, notice.SizeBytes);
            Assert.Equal(Now.AddHours(1), notice.ChangedUtc);
            Assert.False(notice.Gone);
        }
    }

    [Fact]
    public async Task An_agent_that_never_fetched_the_file_is_told_nothing()
    {
        await Place("net of tax");
        await Fetch(Orders, "net of tax");
        await Place("gross of tax");

        Assert.Empty((await Collect(Inventory)).Notices);
    }

    [Fact]
    public async Task Several_changes_before_a_collection_are_one_notice_carrying_the_latest()
    {
        await Place("v1");
        await Fetch(Orders, "v1");

        await Place("v2", Now.AddMinutes(1));
        await Place("v3", Now.AddMinutes(2));

        var notice = Assert.Single((await Collect(Orders)).Notices);
        Assert.Equal(ContentHash.Of("v1"u8), notice.Had);
        Assert.Equal(ContentHash.Of("v3"u8), notice.Now);
    }

    [Fact]
    public async Task A_change_back_to_the_content_fetched_withdraws_the_notice()
    {
        await Place("v1");
        await Fetch(Orders, "v1");

        await Place("v2");
        await Place("v1");

        Assert.Empty((await Collect(Orders)).Notices);
    }

    [Fact]
    public async Task A_second_fetch_moves_what_the_next_notice_compares_against()
    {
        await Place("v1");
        await Fetch(Orders, "v1");
        await Place("v2");
        await Collect(Orders);
        await Fetch(Orders, "v2");

        await Place("v3");

        var notice = Assert.Single((await Collect(Orders)).Notices);
        Assert.Equal(ContentHash.Of("v2"u8), notice.Had);
    }

    [Fact]
    public async Task A_fetch_reported_after_the_file_changed_raises_its_notice_at_once()
    {
        // The report of what the agent received arrives after a replacement landed: waiting for a further change
        // would leave it holding the old content with nothing coming to say so.
        await Place("v1");
        await Place("v2");

        await Fetch(Orders, "v1");

        var notice = Assert.Single((await Collect(Orders)).Notices);
        Assert.Equal(ContentHash.Of("v1"u8), notice.Had);
        Assert.Equal(ContentHash.Of("v2"u8), notice.Now);
    }

    [Fact]
    public async Task A_fetch_of_the_current_content_withdraws_a_notice_already_waiting()
    {
        await Place("v1");
        await Fetch(Orders, "v1");
        await Place("v2");

        await Fetch(Orders, "v2");

        Assert.Empty((await Collect(Orders)).Notices);
    }

    [Fact]
    public async Task Removal_tells_every_fetcher_the_file_is_gone_and_forgets_who_fetched_it()
    {
        await Place("v1");
        await Fetch(Orders, "v1");

        await _files.RemoveAsync(await Stored(), Billing, Now.AddHours(1), cancellationToken: Ct);

        var notice = Assert.Single((await Collect(Orders)).Notices);
        Assert.True(notice.Gone);
        Assert.Null(notice.SizeBytes);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM file_fetches;"));
    }

    [Fact]
    public async Task A_prune_tells_every_fetcher_every_file_is_gone()
    {
        await Place("v1");
        await Fetch(Orders, "v1");

        await _files.RemoveAllAsync(Now, cancellationToken: Ct);

        Assert.True(Assert.Single((await Collect(Orders)).Notices).Gone);
    }

    [Fact]
    public async Task A_rename_tells_the_fetchers_of_the_old_name_it_is_gone()
    {
        await Place("v1");
        await Fetch(Orders, "v1");

        await _files.RenameAsync(await Stored(), SharedFileName.Create("renamed.md"), 2, Now, cancellationToken: Ct);

        var notice = Assert.Single((await Collect(Orders)).Notices);
        Assert.Equal("price-snapshot.md", notice.Name);
        Assert.True(notice.Gone);
    }

    [Fact]
    public async Task Destroying_the_conversation_still_tells_the_fetchers()
    {
        await Place("v1");
        await Fetch(Orders, "v1");

        await new ConversationRepository(_connection).DestroyAsync("pricing", Now, cancellationToken: Ct);

        var notice = Assert.Single((await Collect(Orders)).Notices);
        Assert.Equal("pricing", notice.ConversationId);
        Assert.True(notice.Gone);
    }

    [Fact]
    public async Task Purging_a_participant_tells_the_other_fetchers_and_drops_the_purged_agents_own_records()
    {
        await Place("v1");
        await new ConversationRepository(_connection).JoinByPlacingAsync("pricing", Billing, Now, cancellationToken: Ct);
        await Fetch(Orders, "v1");
        await Fetch(Billing, "v1");

        await new AgentRepository(_connection).PurgeAsync(Billing, Now, cancellationToken: Ct);

        Assert.True(Assert.Single((await Collect(Orders)).Notices).Gone);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM file_notices WHERE agent = 'billing@test';"));
    }

    [Fact]
    public async Task A_retired_agent_is_not_told()
    {
        await Place("v1");
        await Fetch(Orders, "v1");
        Execute("UPDATE agents SET status = 'retired' WHERE name = 'orders@test';");

        await Place("v2");

        Assert.Equal(0, await _notices.CountWaitingAsync(Orders, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_collection_takes_at_most_two_hundred_oldest_first_and_says_how_many_are_left()
    {
        for (var i = 0; i < 250; i++)
        {
            var name = SharedFileName.Create($"file-{i:D3}.md");

            await _files.PlaceAsync("pricing", name, ContentHash.Of("v1"u8), 2, Billing, 1, Now, _ => false, cancellationToken: Ct);
            await _notices.RecordFetchAsync(Orders, "pricing", name, ContentHash.Of("v1"u8), Now, cancellationToken: Ct);
        }

        // Changed in reverse order, so the oldest change is the last file: a collection ordered by name would
        // take the wrong two hundred.
        for (var i = 249; i >= 0; i--)
        {
            var name = SharedFileName.Create($"file-{i:D3}.md");

            await _files.PlaceAsync(
                "pricing", name, ContentHash.Of("v2"u8), 2, Billing, 1, Now.AddSeconds(250 - i), _ => false, cancellationToken: Ct);
        }

        var first = await Collect(Orders);
        Assert.Equal(FileLimits.NoticesPerCollection, first.Notices.Count);
        Assert.Equal(50, first.StillWaiting);
        Assert.Equal("file-249.md", first.Notices[0].Name);
        Assert.True(first.Notices.Zip(first.Notices.Skip(1)).All(p => p.First.ChangedUtc <= p.Second.ChangedUtc));

        var second = await Collect(Orders);
        Assert.Equal(50, second.Notices.Count);
        Assert.Equal(0, second.StillWaiting);
        Assert.Empty(first.Notices.Select(n => n.Name).Intersect(second.Notices.Select(n => n.Name)));

        Assert.Empty((await Collect(Orders)).Notices);
    }

    [Fact]
    public async Task A_collected_notice_is_gone_and_the_fetch_record_stays_so_a_further_change_raises_another()
    {
        await Place("v1");
        await Fetch(Orders, "v1");
        await Place("v2");

        Assert.Single((await Collect(Orders)).Notices);
        Assert.Equal(0, await _notices.CountWaitingAsync(Orders, cancellationToken: Ct));

        await Place("v3");

        Assert.Single((await Collect(Orders)).Notices);
    }

    private async Task Place(string content, DateTimeOffset? at = null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);

        await _files.PlaceAsync(
            "pricing",
            SharedFileName.Create("price-snapshot.md"),
            ContentHash.Of(bytes),
            bytes.Length,
            Billing,
            modifiedTicks: 1,
            at ?? Now,
            takenOnDisk: _ => false,
            cancellationToken: Ct);
    }

    private Task Fetch(AgentName agent, string content) =>
        _notices.RecordFetchAsync(
            agent,
            "pricing",
            SharedFileName.Create("price-snapshot.md"),
            ContentHash.Of(System.Text.Encoding.UTF8.GetBytes(content)),
            Now,
            cancellationToken: Ct);

    private Task<FileNoticeBatch> Collect(AgentName agent) => _notices.CollectAsync(agent, cancellationToken: Ct);

    private async Task<StoredFile> Stored() =>
        (await _files.GetAsync("pricing", SharedFileName.Create("price-snapshot.md"), cancellationToken: Ct))!;

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
