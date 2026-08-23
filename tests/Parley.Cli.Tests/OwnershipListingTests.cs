using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the operator's listing shows what each agent owns, and says plainly when one does not.
/// </summary>
/// <remarks>
/// The field is optional and the feature is inert if nobody sets it, so something has to tell
/// the person who can set it that nobody has. This listing is that something, and it was chosen
/// over a note at start-up deliberately: a server's standard error goes to whichever log its
/// client keeps for a server that is misbehaving, which nobody reads while nothing is wrong, and
/// it would fire on every launch of every deployment that left the variable unset on purpose.
/// </remarks>
public sealed class OwnershipListingTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly StringWriter _output = new();
    private readonly OperatorCommands _commands;

    public OwnershipListingTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);
        _commands = new OperatorCommands(new OperatorConsole(_connection), _output);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _output.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<int> Register(AgentName name, string? owns) =>
        _agents.UpsertAsync(
            new AgentRecord(name, "mcp", AgentTier.Pull, $"/src/acme-{name.Value}", null, null,
                null, AgentStatus.Registered, owns),
            Ct);

    [Fact]
    public async Task The_listing_shows_what_an_agent_owns()
    {
        await Register(Billing, "Invoicing and tax. Does not price orders.");

        Assert.Equal(0, await _commands.AgentsAsync(Ct));

        Assert.Contains(
            "Invoicing and tax. Does not price orders.",
            _output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_that_has_not_said_is_reported_as_not_having_said()
    {
        // Printed rather than skipped. A blank line would read as an agent that owns nothing,
        // and a missing line would read as an agent that is fine.
        await Register(Orders, null);

        await _commands.AgentsAsync(Ct);

        var printed = _output.ToString();

        Assert.Contains("unset", printed, StringComparison.Ordinal);

        // It names the variable, because the person reading this is about to go and set it and
        // should not have to find out what it is called from somewhere else.
        Assert.Contains(DeploymentEnvironment.OwnsVariable, printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_operator_sees_the_statement_and_the_workspace_together()
    {
        // The two halves of telling one agent from another: where it works, which no peer sees,
        // and what it claims, which every peer does.
        await Register(Billing, "Invoicing and tax.");

        await _commands.AgentsAsync(Ct);

        var printed = _output.ToString();

        Assert.Contains("/src/acme-billing", printed, StringComparison.Ordinal);
        Assert.Contains("Invoicing and tax.", printed, StringComparison.Ordinal);
    }
}
