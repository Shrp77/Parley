using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Gone notices: who is told, what they are told, and when a notice stops waiting (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Each case is a way an agent could keep building on a file that has gone without knowing</b> — a removal nobody
/// raised a notice for, a notice raised for an agent that never fetched the file, or one that outlives the agent's
/// fetching the name again. A placed file never changes, so its going is the only thing noticed.
/// </para>
/// <para>
/// Retired agents appear on purpose. Active agents are told, and a retired agent that
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
    public async Task Every_agent_that_fetched_a_file_is_told_when_it_goes_with_the_hash_it_had()
    {
        await Place("net of tax");
        await Fetch(Orders, "net of tax");
        await Fetch(Inventory, "net of tax");

        await _files.RemoveAsync(await Stored(), Billing, Now.AddHours(1), cancellationToken: Ct);

        foreach (var agent in new[] { Orders, Inventory })
        {
            var notice = Assert.Single((await Collect(agent)).Notices);
            Assert.Equal(ContentHash.Of("net of tax"u8), notice.Had);
            Assert.Equal(Now.AddHours(1), notice.GoneUtc);
            Assert.Equal("price-snapshot.md", notice.Name);
        }
    }

    [Fact]
    public async Task An_agent_that_never_fetched_the_file_is_told_nothing()
    {
        await Place("v1");
        await Fetch(Orders, "v1");

        await _files.RemoveAsync(await Stored(), Billing, Now, cancellationToken: Ct);

        Assert.Empty((await Collect(Inventory)).Notices);
    }

    [Fact]
    public async Task A_fetch_of_the_name_placed_again_withdraws_a_notice_already_waiting()
    {
        // The agent holds the file now under the name; telling it later that the old one went would be untrue of it.
        await Place("v1");
        await Fetch(Orders, "v1");
        await _files.RemoveAsync(await Stored(), Billing, Now, cancellationToken: Ct);

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

        Assert.Single((await Collect(Orders)).Notices);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM file_fetches;"));
    }

    [Fact]
    public async Task A_prune_tells_every_fetcher_every_file_is_gone()
    {
        await Place("v1");
        await Fetch(Orders, "v1");

        await _files.RemoveAllAsync(Now, cancellationToken: Ct);

        Assert.Single((await Collect(Orders)).Notices);
    }

    [Fact]
    public async Task Destroying_the_conversation_still_tells_the_fetchers()
    {
        await Place("v1");
        await Fetch(Orders, "v1");

        await new ConversationRepository(_connection).DestroyAsync("pricing", Now, cancellationToken: Ct);

        Assert.Equal("pricing", Assert.Single((await Collect(Orders)).Notices).ConversationId);
    }

    [Fact]
    public async Task Purging_a_participant_tells_the_other_fetchers_and_drops_the_purged_agents_own_records()
    {
        await Place("v1");
        await new ConversationRepository(_connection).JoinByPlacingAsync("pricing", Billing, Now, cancellationToken: Ct);
        await Fetch(Orders, "v1");
        await Fetch(Billing, "v1");

        await new AgentRepository(_connection).PurgeAsync(Billing, Now, cancellationToken: Ct);

        Assert.Single((await Collect(Orders)).Notices);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM file_notices WHERE agent = 'billing@test';"));
    }

    [Fact]
    public async Task A_retired_agent_is_not_told()
    {
        await Place("v1");
        await Fetch(Orders, "v1");
        Execute("UPDATE agents SET status = 'retired' WHERE name = 'orders@test';");

        await _files.RemoveAsync(await Stored(), Billing, Now, cancellationToken: Ct);

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

        // Removed in reverse order, so the oldest going is the last file: a collection ordered by name would take the
        // wrong two hundred.
        for (var i = 249; i >= 0; i--)
        {
            var name = SharedFileName.Create($"file-{i:D3}.md");
            var stored = (await _files.GetAsync("pricing", name, cancellationToken: Ct))!;

            await _files.RemoveAsync(stored, Billing, Now.AddSeconds(250 - i), cancellationToken: Ct);
        }

        var first = await Collect(Orders);
        Assert.Equal(FileLimits.NoticesPerCollection, first.Notices.Count);
        Assert.Equal(50, first.StillWaiting);
        Assert.Equal("file-249.md", first.Notices[0].Name);
        Assert.True(first.Notices.Zip(first.Notices.Skip(1)).All(p => p.First.GoneUtc <= p.Second.GoneUtc));

        var second = await Collect(Orders);
        Assert.Equal(50, second.Notices.Count);
        Assert.Equal(0, second.StillWaiting);
        Assert.Empty(first.Notices.Select(n => n.Name).Intersect(second.Notices.Select(n => n.Name)));

        Assert.Empty((await Collect(Orders)).Notices);
    }

    [Fact]
    public async Task A_tampered_file_tells_its_fetchers_and_its_placer_with_the_hash_found()
    {
        // Something other than Parley changed the bytes: whoever holds a copy, and whoever can place it again, is told.
        await Place("v1");
        await Fetch(Orders, "v1");

        Assert.True(await _files.RemoveTamperedAsync(await Stored(), ContentHash.Of("v2"u8), 2, Now.AddHours(1), cancellationToken: Ct));

        foreach (var agent in new[] { Orders, Billing })
        {
            var notice = Assert.Single((await Collect(agent)).Notices);
            Assert.True(notice.Tampered);
            Assert.Equal(ContentHash.Of("v1"u8), notice.Had);
            Assert.Equal(ContentHash.Of("v2"u8), notice.Found);
        }

        Assert.Empty((await Collect(Inventory)).Notices);
        Assert.Null(await _files.GetAsync("pricing", SharedFileName.Create("price-snapshot.md"), cancellationToken: Ct));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM file_fetches;"));
    }

    [Fact]
    public async Task A_file_found_missing_tells_its_fetchers_and_its_placer_and_a_removal_tells_only_the_fetchers()
    {
        // Something other than Parley took the file, so its placer, who can place it again, is told; a removal Parley
        // made was asked for by its placer or the person, who know.
        await Place("v1");
        await Fetch(Orders, "v1");

        Assert.True(await _files.RemoveMissingAsync(await Stored(), Now, cancellationToken: Ct));

        Assert.False(Assert.Single((await Collect(Orders)).Notices).Tampered);
        Assert.Equal(ContentHash.Of("v1"u8), Assert.Single((await Collect(Billing)).Notices).Had);

        await Place("v2", Now.AddMinutes(1));
        await Fetch(Orders, "v2");
        await _files.RemoveAsync(await Stored(), null, Now.AddMinutes(2), cancellationToken: Ct);

        Assert.Single((await Collect(Orders)).Notices);
        Assert.Empty((await Collect(Billing)).Notices);
    }

    [Fact]
    public async Task A_placer_that_also_fetched_its_file_is_told_once()
    {
        await Place("v1");
        await Fetch(Billing, "v1");

        Assert.True(await _files.RemoveTamperedAsync(await Stored(), ContentHash.Of("v2"u8), 2, Now, cancellationToken: Ct));

        Assert.True(Assert.Single((await Collect(Billing)).Notices).Tampered);
    }

    [Fact]
    public async Task A_tampered_removal_of_a_file_placed_again_since_removes_nothing()
    {
        // The sweep looked before the name was removed and placed again: the file it found is not the one there now.
        await Place("v1");
        var looked = await Stored();
        await _files.RemoveAsync(looked, Billing, Now, cancellationToken: Ct);
        await Place("v2", Now.AddMinutes(1));

        Assert.False(await _files.RemoveTamperedAsync(looked, ContentHash.Of("v3"u8), 2, Now.AddMinutes(2), cancellationToken: Ct));
        Assert.NotNull(await _files.GetAsync("pricing", SharedFileName.Create("price-snapshot.md"), cancellationToken: Ct));
    }

    [Fact]
    public async Task A_collected_notice_is_gone()
    {
        await Place("v1");
        await Fetch(Orders, "v1");
        await _files.RemoveAsync(await Stored(), Billing, Now, cancellationToken: Ct);

        Assert.Single((await Collect(Orders)).Notices);
        Assert.Equal(0, await _notices.CountWaitingAsync(Orders, cancellationToken: Ct));
        Assert.Empty((await Collect(Orders)).Notices);
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

    // Read and deleted in one act, as an acknowledged collection does.
    private async Task<FileNoticeBatch> Collect(AgentName agent)
    {
        var (batch, keys) = await _notices.PeekAsync(agent, cancellationToken: Ct);
        await _notices.DeleteAsync(agent, keys, Ct);

        return batch;
    }

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
