using System.Net;
using System.Net.Sockets;
using Parley.Configuration;
using Parley.Core;
using Xunit;
using static Parley.Cli.Tests.Loopback;

namespace Parley.Cli.Tests;

/// <summary>
/// That a copy of a store taken before an upgrade is named when a start fails and removed once a start serves,
/// whichever start took it (SPEC.md §9.2).
/// </summary>
/// <remarks>
/// <b>Found by review, round 37.</b> Only the start that ran the migration knew its copy, so one whose listen or
/// later preparation failed left a copy holding every conversation's text that no later start named or removed.
/// </remarks>
public sealed class ACopyTakenBeforeAnUpgradeIsNeverKeptTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-copy-kept-" + Guid.NewGuid().ToString("N"));

    public ACopyTakenBeforeAnUpgradeIsNeverKeptTests() => Directory.CreateDirectory(_root);

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

    private static void SchemaOne(string store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);

        using (var connection = Parley.Store.ParleyDatabase.Open(store))
        {
            var assembly = typeof(Parley.Store.SchemaMigrator).Assembly;
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".001_initial.sql", StringComparison.Ordinal));

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            using var command = connection.CreateCommand();

            command.CommandText = reader.ReadToEnd() + "\nPRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store}"));
    }

    [Fact]
    public async Task A_copy_kept_by_a_start_that_could_not_listen_is_removed_once_the_broker_serves()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = ProjectName.Create("orders");
        var store = ProjectFolder.StoreFor(_root, name);
        SchemaOne(store);

        var port = FreePort();
        var orders = new ProjectSettings(name, store, $"http://127.0.0.1:{port}", null);
        ProjectFolder.Write(_root, orders);
        Assert.True(ProjectManifest.TryRead(_root, out var initial, out _));

        // First start: the port is still held (the previous broker at logon), so the upgrade happens and the listen fails.
        var firstOut = new StringWriter();
        var firstErr = new StringWriter();

        using (var holder = new TcpListener(IPAddress.Loopback, port))
        {
            holder.Start();
            var code = await ServeCommand.RunAsync(initial, null, firstOut, firstErr, cancellationToken: ct)
                .WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.NotEqual(0, code);
        }

        var kept = Directory.GetFiles(Path.GetDirectoryName(store)!, "*" + StoreFiles.BeforeSchema + "*");
        Assert.Single(kept);

        // Second start, the port free: the upgraded broker serves.
        var secondOut = new StringWriter();
        var secondErr = new StringWriter();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serving = ServeCommand.RunAsync(initial, null, secondOut, secondErr, cancellationToken: stopping.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct), "never came up: " + secondErr);
            await Task.Delay(500, ct);
        }
        finally
        {
            await stopping.CancelAsync();
        }

        _ = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        var left = Directory.GetFiles(Path.GetDirectoryName(store)!, "*" + StoreFiles.BeforeSchema + "*");
        Assert.True(
            left.Length == 0,
            $"The upgraded broker served and the copy is still there: {string.Join(", ", left)}\nfirst err: {firstErr}\nsecond out: {secondOut}\nsecond err: {secondErr}");
    }

    [Fact]
    public async Task A_start_that_fails_after_the_upgrade_names_the_copy_it_kept()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = ProjectName.Create("orders");
        var store = ProjectFolder.StoreFor(_root, name);
        SchemaOne(store);

        // The file location cannot be made (a file where its folder goes), which fails the start after the migration.
        await File.WriteAllTextAsync(store + StoreFiles.Files, "in the way", ct);

        var orders = new ProjectSettings(name, store, $"http://127.0.0.1:{FreePort()}", null);
        ProjectFolder.Write(_root, orders);
        Assert.True(ProjectManifest.TryRead(_root, out var initial, out _));

        var output = new StringWriter();
        var error = new StringWriter();
        _ = await ServeCommand.RunAsync(initial, null, output, error, cancellationToken: ct).WaitAsync(TimeSpan.FromSeconds(30), ct);

        var kept = Directory.GetFiles(Path.GetDirectoryName(store)!, "*" + StoreFiles.BeforeSchema + "*");
        Assert.Single(kept);
        Assert.True(
            (output.ToString() + error).Contains(kept[0], StringComparison.Ordinal),
            $"A copy was kept at {kept[0]} and nothing said so.\nout: {output}\nerr: {error}");

        // The person's next step differs by whether the store was upgraded, so the words say which (docs/operating.md).
        Assert.Contains("The store was upgraded to schema ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_copy_from_an_earlier_failed_migration_is_removed_when_a_later_upgrade_serves()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = ProjectName.Create("orders");
        var store = ProjectFolder.StoreFor(_root, name);
        SchemaOne(store);

        // What an earlier attempt that failed inside the migration (rolled back) left: a copy under an earlier second.
        var earlier = store + StoreFiles.BeforeSchema + "2-20261027T093000Z";
        File.Copy(store, earlier);

        var orders = new ProjectSettings(name, store, $"http://127.0.0.1:{FreePort()}", null);
        ProjectFolder.Write(_root, orders);
        Assert.True(ProjectManifest.TryRead(_root, out var initial, out _));

        var output = new StringWriter();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serving = ServeCommand.RunAsync(initial, null, output, new StringWriter(), cancellationToken: stopping.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct), "never came up");
            await Task.Delay(500, ct);
        }
        finally
        {
            await stopping.CancelAsync();
        }

        _ = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        var left = Directory.GetFiles(Path.GetDirectoryName(store)!, "*" + StoreFiles.BeforeSchema + "*");
        Assert.True(left.Length == 0, $"Served, said: {output}\nleft: {string.Join(", ", left)}");
    }
}
