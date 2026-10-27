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

    [Fact]
    public async Task An_upgraded_store_is_served_and_the_copy_taken_before_is_removed()
    {
        // Round 36, the owner's rule: the copy is kept only until the upgraded broker serves, because it holds every
        // conversation's text, which destroy, purge and retention exist to remove.
        var store = ProjectFolder.StoreFor(_root, ProjectName.Create("orders"));
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

        const string upgraded = "orders: its store was upgraded to schema ";
        var said = await ServeAsync(heartbeat: false, TimeSpan.FromMilliseconds(500), upgraded);

        Assert.Contains(upgraded, said, StringComparison.Ordinal);
        Assert.Contains("the copy taken before the upgrade was removed", said, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store)!, "*" + StoreFiles.BeforeSchema + "*"));
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
