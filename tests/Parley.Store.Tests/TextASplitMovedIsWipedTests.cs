using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That a destroyed conversation's text is gone from the store's file even where SQLite moved a row while it lived
/// (SPEC.md §9.1).
/// </summary>
/// <remarks>
/// Found by review, lens 2 run 116. Secure deletion zeroes a row where it lives when it is deleted, but a page split or
/// rebalance copies rows to a sibling page and leaves the old bytes in the original page's free gap, which nothing then
/// overwrites. Every message grows when it is delivered, so splits in the middle of the messages table are ordinary,
/// and about one message in a few thousand survived its conversation's destruction. <see cref="DeletedTextLeavesTheFileTests"/>
/// writes one message, which never splits anything.
/// </remarks>
[Integration]
public sealed class TextASplitMovedIsWipedTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), "parley-split-" + Guid.NewGuid().ToString("N") + ".db");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // The temp folder takes it from here.
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Nothing_of_a_destroyed_conversation_is_left_in_the_file(int seed)
    {
        var random = new Random(seed);
        var agents = Enumerable.Range(0, 6).Select(i => AgentName.Create($"agent{i}@test")).ToArray();
        var conversations = Enumerable.Range(0, 30).Select(i => $"conversation-{i}").ToArray();
        var destroyed = new HashSet<string>(conversations.Where((_, i) => i % 2 == 0), StringComparer.Ordinal);
        var markers = new List<(string Conversation, string Marker)>();

        using var connection = ParleyDatabase.OpenAndMigrate(_path);
        Work.Register(connection, agents);
        var messages = new MessageRepository(connection);
        var undelivered = new Queue<long>();

        for (var i = 0; i < 1500; i++)
        {
            var from = agents[random.Next(agents.Length)];
            var to = agents[(Array.IndexOf(agents, from) + 1 + random.Next(agents.Length - 1)) % agents.Length];
            var conversation = conversations[random.Next(conversations.Length)];
            var marker = "split-marker-" + i.ToString("D5", CultureInfo.InvariantCulture) + "-zq";
            var filler = new string('a', random.Next(40, 3000));
            var at = T0.AddSeconds(i);

            var (exchange, request) = await Work.OpenAsync(
                connection, from, to, at, Work.Request(marker + " " + filler), conversation, cancellationToken: Ct);
            _ = await Work.CloseAsync(connection, exchange, at.AddMilliseconds(500), cancellationToken: Ct);
            markers.Add((conversation, marker));

            // Delivered some exchanges later, as collection is: the row grows where it sits, and pages split.
            undelivered.Enqueue(request.Id);

            if (undelivered.Count > 20)
            {
                _ = await messages.MarkDeliveredAsync(undelivered.Dequeue(), at, Ct);
            }
        }

        var repository = new ConversationRepository(connection);

        foreach (var conversation in destroyed)
        {
            _ = await repository.DestroyAsync(conversation, T0.AddDays(1), cancellationToken: Ct);
        }

        var store = Read(_path);
        var log = Read(_path + "-wal");
        var left = markers
            .Where(m => destroyed.Contains(m.Conversation))
            .Select(m => m.Marker)
            .Where(marker => Contains(store, marker) || Contains(log, marker))
            .ToList();

        Assert.True(
            markers.Any(m => !destroyed.Contains(m.Conversation) && Contains(store, m.Marker)),
            "no kept conversation's text is in the file, so the scan proves nothing");
        Assert.Empty(left);
    }

    [Fact]
    public async Task A_rewrite_that_cannot_run_does_not_fail_the_act_that_called_it()
    {
        // A disk without room for the rewrite's copy is what this stands for; a statement still reading on the
        // connection refuses the rewrite and the checkpoint alike, and is the one a test can arrange. The act before
        // the flush has committed, so the flush must not turn it into an error.
        using (var store = ParleyDatabase.OpenAndMigrate(_path))
        {
            Work.Register(store, AgentName.Create("orders@test"), AgentName.Create("billing@test"));
            _ = await Work.OpenAsync(store, AgentName.Create("orders@test"), AgentName.Create("billing@test"), T0, cancellationToken: Ct);
        }

        // Refused within a second rather than after the client's thirty seconds of retrying a locked statement.
        using var connection = new SqliteConnection($"Data Source={_path};Pooling=False;Default Timeout=1");
        connection.Open();

        using var reading = connection.CreateCommand();
        reading.CommandText = "SELECT name FROM agents;";
        using var reader = reading.ExecuteReader();
        Assert.True(reader.Read());

        ParleyDatabase.Flush(connection);
    }

    private static byte[] Read(string file)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);

        return copy.ToArray();
    }

    private static bool Contains(byte[] bytes, string marker) =>
        bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0;
}
