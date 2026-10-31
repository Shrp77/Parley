using Parley.Broker;
using Parley.Configuration;
using Parley.Core;
using Parley.Server;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the hourly retention pass prunes under the policy the broker was configured with, the one a person's prune
/// reports (SPEC.md §9.4).
/// </summary>
/// <remarks>
/// Serve opens a runtime with the default options and the configured policy beside them, and the hourly pass once read
/// the options': a deployment that kept text for ninety days still lost it at thirty, every hour, while a person's prune
/// said ninety.
/// </remarks>
[Integration]
public sealed class TheHourlyPassKeepsTheConfiguredPolicyTests : IDisposable
{
    private static readonly RetentionPolicy Configured = new(TimeSpan.FromHours(1000), TimeSpan.FromHours(2160));

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-hourly-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a pooled connection can still hold the file.
        }
    }

    [Fact]
    public async Task A_runtime_opened_as_serve_opens_it_prunes_hourly_under_the_configured_policy()
    {
        var name = ProjectName.Create("test");
        var store = ProjectFolder.StoreFor(_directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);

        await using var runtime = BrokerRuntime.Open(
            name, store, BrokerOptions.Default, TimeProvider.System, Configured);

        var tick = await runtime.TickAsync(Ct);

        Assert.Equal(Configured, tick.Retention.Applied);
        Assert.Equal(Configured, runtime.Retention);
    }

    [Fact]
    public async Task A_runtime_given_no_policy_prunes_under_the_one_its_options_name()
    {
        var options = BrokerOptions.Default with { Retention = Configured };

        await using var runtime = BrokerRuntime.OpenInMemory(options, TimeProvider.System);

        var tick = await runtime.TickAsync(Ct);

        Assert.Equal(Configured, tick.Retention.Applied);
        Assert.Equal(Configured, runtime.Retention);
    }

    [Fact]
    public async Task A_runtime_over_a_connection_given_no_policy_prunes_under_the_one_its_options_name()
    {
        var options = BrokerOptions.Default with { Retention = Configured };
        using var connection = ParleyDatabase.OpenInMemory();

        await using var runtime = BrokerRuntime.Over(connection, options, TimeProvider.System);

        var tick = await runtime.TickAsync(Ct);

        Assert.Equal(Configured, tick.Retention.Applied);
        Assert.Equal(Configured, runtime.Retention);
    }
}
