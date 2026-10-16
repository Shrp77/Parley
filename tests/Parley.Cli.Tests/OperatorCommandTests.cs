using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// The registry listing, the one operator operation the command line still performs.
/// </summary>
/// <remarks>
/// These drive the command rather than the process, so what is pinned is what it does and what
/// it says. The entry point keeps only argument shape and exit codes.
/// </remarks>
public sealed class OperatorCommandTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private readonly SqliteConnection _connection;
    private readonly StringWriter _output = new();
    private readonly LoopbackOperator _broker;
    private readonly OperatorCommands _commands;

    public OperatorCommandTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, $"/src/acme-{name.Value}", null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
        _commands = new OperatorCommands(_broker.Operators, _output);
    }

    public async ValueTask DisposeAsync()
    {
        // The broker first: it borrowed this connection and its shutdown writes to it.
        await _broker.DisposeAsync();

        _connection.Dispose();
        _output.Dispose();
    }

    private string Printed => _output.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Listing_agents_shows_what_a_peer_is_never_shown()
    {
        // §10.1 rule 3 keeps the workspace out of the agent-facing listing, because a peer has
        // no business knowing where another repository lives. An operator is looking at their
        // own deployment, so this is the projection that does show it — which is exactly why the
        // two operations are named apart.
        Assert.Equal(0, await _commands.RegistryAsync(Ct));

        Assert.Contains("orders@test", Printed, StringComparison.Ordinal);
        Assert.Contains("/src/acme-orders", Printed, StringComparison.Ordinal);
        Assert.Contains("stranded", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_registry_says_how_one_gets_filled()
    {
        using var connection = ParleyDatabase.OpenInMemory();
        using var output = new StringWriter();

        await using var broker = LoopbackOperator.Start(connection);

        var commands = new OperatorCommands(broker.Operators, output);

        Assert.Equal(0, await commands.RegistryAsync(Ct));
        Assert.Contains("first time its server starts", output.ToString(), StringComparison.Ordinal);
    }
}
