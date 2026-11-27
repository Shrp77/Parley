using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That retiring an agent takes its launch entry and catalogue line only when the broker retired it, and never reads
/// the registry's placeholder for no workspace as a path.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentWithdrawal"/> is the one implementation both screens call, so it is held here directly, over a real
/// broker on a loopback socket, rather than through either screen.
/// </para>
/// <para>
/// A retire the broker refused leaves the agent registered and able to start. Taking its files anyway would leave a
/// registered agent nothing can launch, with nothing on screen saying why.
/// </para>
/// </remarks>
[Integration]
public sealed class AWithdrawalTakesFilesOnlyAfterTheBrokerActedTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-withdrawal-" + Path.GetRandomFileName());

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public AWithdrawalTakesFilesOnlyAfterTheBrokerActedTests()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

        _connection = ParleyDatabase.OpenInMemory();
        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Workspace => Path.Combine(_root, "orders");

    private string CataloguePath => Path.Combine(_root, NodeCatalogue.DefaultFileName);

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    [Fact]
    public async Task A_retire_the_broker_refused_leaves_the_launch_entry_and_the_catalogue_line()
    {
        // Nothing is registered under this name, so the broker changes nothing and says so.
        var identity = await WriteFilesAsync();

        var said = await AgentWithdrawal.RetireAsync(
            new BrokerClient(_broker.Operators), Orders, Workspace, null, CataloguePath, Ct);

        Assert.Contains("'orders@test' was not registered", said, StringComparison.Ordinal);
        Assert.True(await HasLaunchEntryAsync(), "a refused retire took the launch entry");
        Assert.Contains(identity.ToString(), await File.ReadAllTextAsync(CataloguePath, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_registrys_placeholder_for_no_workspace_is_not_read_as_a_path()
    {
        // The registry shows a dash where it holds no workspace, and a screen hands its row's text on as it is.
        await new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active), Ct);

        var said = await AgentWithdrawal.RetireAsync(
            new BrokerClient(_broker.Operators), Orders, "—", null, CataloguePath, Ct);

        Assert.Contains("Retired 'orders@test'", said, StringComparison.Ordinal);
        Assert.Contains("No workspace is recorded for it, so there was no launch configuration to remove.", said, StringComparison.Ordinal);
    }

    /// <summary>Writes the repository's launch entry and this machine's catalogue line, keyed to each other.</summary>
    private async Task<AgentIdentity> WriteFilesAsync()
    {
        var identity = AgentIdentity.Mint();

        await File.WriteAllTextAsync(
            CataloguePath,
            "{\n  \"agents\": {\n    \"" + identity + "\": { \"command\": \"claude\", \"workspace\": \""
            + Workspace.Replace("\\", "\\\\", StringComparison.Ordinal) + "\" }\n  }\n}\n",
            Ct);

        await File.WriteAllTextAsync(
            LaunchFile,
            $$"""
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } }
              },
              "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
            }
            """,
            Ct);

        return identity;
    }

    private async Task<bool> HasLaunchEntryAsync()
    {
        var launch = JsonNode.Parse(await File.ReadAllTextAsync(LaunchFile, Ct))!;

        return ((JsonObject)launch["mcpServers"]!).ContainsKey("parley");
    }
}
