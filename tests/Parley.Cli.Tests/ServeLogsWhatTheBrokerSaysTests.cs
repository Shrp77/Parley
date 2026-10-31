using System.Runtime.InteropServices;
using Parley.Configuration;
using Parley.Core;
using Xunit;
using static Parley.Cli.Tests.Loopback;

namespace Parley.Cli.Tests;

/// <summary>
/// That what a broker logs reaches <c>serve --log</c>, and that <c>--heartbeat</c> adds a line a second.
/// </summary>
/// <remarks>
/// A broker logged to the console, and a broker started hidden at logon has none, so what it logged went nowhere.
/// </remarks>
[Integration]
public sealed class ServeLogsWhatTheBrokerSaysTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-serve-log-" + Guid.NewGuid().ToString("N"));

    public ServeLogsWhatTheBrokerSaysTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string ReadShared(string log)
    {
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    // Serves for about forAbout, and past it until the log holds every one of until, for at most thirty seconds: a
    // loaded runner can starve the heartbeat's first beat past any fixed wait (Windows CI did, past 2.5 s).
    private async Task<string> ServeAsync(bool heartbeat, TimeSpan forAbout, params string[] until)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = ProjectName.Create("orders");
        var orders = new ProjectSettings(name, ProjectFolder.StoreFor(_root, name), $"http://127.0.0.1:{FreePort()}", null);
        ProjectFolder.Write(_root, orders);

        Assert.True(ProjectManifest.TryRead(_root, out var initial, out _));

        var log = Path.Combine(_root, "broker.log");
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            initial, log, new StringWriter(), new StringWriter(), heartbeat: heartbeat, cancellationToken: stopping.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct), "never came up");
            await Task.Delay(forAbout, ct);

            var giveUp = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

            while (until.Length > 0 && DateTimeOffset.UtcNow < giveUp
                && !until.All(line => ReadShared(log).Contains(line, StringComparison.Ordinal)))
            {
                await Task.Delay(100, ct);
            }
        }
        finally
        {
            await stopping.CancelAsync();
        }

        _ = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        return await File.ReadAllTextAsync(log, ct);
    }

    [Fact]
    public async Task The_brokers_own_lines_reach_the_log_and_a_heartbeat_only_when_asked()
    {
        var plain = await ServeAsync(heartbeat: false, TimeSpan.FromMilliseconds(1500));

        Assert.Contains("orders Parley.Server: listening on http://127.0.0.1:", plain, StringComparison.Ordinal);
        Assert.Contains("orders Parley.Server: stopped", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Parley.Server.Heartbeat", plain, StringComparison.Ordinal);

        File.Delete(Path.Combine(_root, "broker.log"));

        const string beat = "orders Parley.Server.Heartbeat: heartbeat 1: up ";
        const string connection = "orders Parley.Server.Connections: connection ";
        var beating = await ServeAsync(heartbeat: true, TimeSpan.FromMilliseconds(1500), beat, connection);

        Assert.Contains(beat, beating, StringComparison.Ordinal);
        Assert.Contains(connection, beating, StringComparison.Ordinal);
    }

    // A store at schema 1 where serve will find it, with what else the case needs written into it.
    private string SchemaOneStore(string extraSql = "")
    {
        var store = ProjectFolder.StoreFor(_root, ProjectName.Create("orders"));
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);

        using (var connection = Parley.Store.ParleyDatabase.Open(store))
        {
            var assembly = typeof(Parley.Store.SchemaMigrator).Assembly;
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".001_initial.sql", StringComparison.Ordinal));

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            using var command = connection.CreateCommand();

            command.CommandText = reader.ReadToEnd() + Environment.NewLine + "PRAGMA user_version = 1;" + Environment.NewLine + extraSql;
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store}"));

        return store;
    }

    [Fact]
    public async Task An_upgraded_store_is_served_and_every_copy_taken_before_is_removed()
    {
        // The owner's rule: a copy is kept only until the upgraded store is served, because it holds every conversation's
        // text, which destroy, purge and retention exist to remove. A copy an earlier, failed start left goes too.
        var store = SchemaOneStore();
        var earlier = store + StoreFiles.BeforeSchema + "2-20260101T000000Z";
        await File.WriteAllTextAsync(earlier, "a copy an earlier start left", TestContext.Current.CancellationToken);

        const string upgraded = "orders: its store was upgraded to schema ";
        var said = await ServeAsync(heartbeat: false, TimeSpan.FromMilliseconds(500), upgraded, "removed, now that it serves");

        Assert.Contains(upgraded, said, StringComparison.Ordinal);
        Assert.Contains("orders: 2 copies of its store taken before an upgrade removed", said, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store)!, "*" + StoreFiles.BeforeSchema + "*"));
    }

    [Fact]
    public async Task A_migration_that_fails_says_where_the_copy_is_and_leaves_the_store_as_it_was()
    {
        var ct = TestContext.Current.CancellationToken;

        // A reference made by hand with foreign keys off: carrying the proposal's items across then fails the migration.
        var store = SchemaOneStore("""
            PRAGMA foreign_keys = OFF;
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00');
            INSERT INTO agents (name, adapter, tier, status) VALUES ('orders@test', 'claude-code', 'full', 'active');
            INSERT INTO roadmap_proposals (id, proposer, title, size, state, created_utc)
            VALUES (1, 'orders@test', 'Plan', 4, 'waiting', '2026-01-01T00:00:00Z');
            INSERT INTO roadmap_proposal_items (proposal_id, position, text, intended_agent) VALUES (1, 1, 'Do it', 'gone@test');
            """);

        var name = ProjectName.Create("orders");
        var orders = new ProjectSettings(name, store, $"http://127.0.0.1:{FreePort()}", null);
        ProjectFolder.Write(_root, orders);
        Assert.True(ProjectManifest.TryRead(_root, out var initial, out _));

        var error = new StringWriter();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serving = ServeCommand.RunAsync(initial, null, new StringWriter(), error, cancellationToken: stopping.Token);

        var giveUp = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

        while (!error.ToString().Contains("could not open", StringComparison.Ordinal) && DateTimeOffset.UtcNow < giveUp)
        {
            await Task.Delay(100, ct);
        }

        await stopping.CancelAsync();
        _ = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        var copy = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store)!, "*" + StoreFiles.BeforeSchema + "*"));
        Assert.Contains(
            $"The store was not upgraded and is as it was; a copy of it as it was before this start is at {copy}",
            error.ToString(),
            StringComparison.Ordinal);

        using var kept = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store};Mode=ReadOnly;Pooling=False");
        kept.Open();
        using var version = kept.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, (long)version.ExecuteScalar()!);
    }

    [Theory]
    [InlineData(PosixSignal.SIGINT, "Ctrl+C")]
    [InlineData(PosixSignal.SIGHUP, "its console window being closed")]
    [InlineData(PosixSignal.SIGTERM, "logoff or shutdown")]
    public void A_signal_is_named_as_a_person_on_Windows_would_know_it(PosixSignal signal, string onWindows)
    {
        Assert.Equal(OperatingSystem.IsWindows() ? onWindows : signal.ToString(), ProcessWatch.Describe(signal));
    }
}
