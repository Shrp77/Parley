using Microsoft.Data.Sqlite;
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
public sealed class OwnershipListingTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly StringWriter _output = new();
    private readonly LoopbackOperator _broker;
    private readonly OperatorCommands _commands;

    public OwnershipListingTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<int> Register(AgentName name, string? owns) =>
        _agents.UpsertAsync(
            new AgentRecord(name, "mcp", AgentTier.Pull, $"/src/acme-{name.Value}", null, null, false, AgentStatus.Active, owns),
            Ct);

    [Fact]
    public async Task The_listing_shows_what_an_agent_owns()
    {
        await Register(Billing, "Invoicing and tax. Does not price orders.");

        Assert.Equal(0, await _commands.RegistryAsync(Ct));

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

        await _commands.RegistryAsync(Ct);

        var printed = _output.ToString();

        Assert.Contains("unset", printed, StringComparison.Ordinal);

        // It names the variable, because the person reading this is about to go and set it and
        // should not have to find out what it is called from somewhere else.
        Assert.Contains(DeploymentEnvironment.OwnsVariable, printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_accepted_and_not_yet_live_is_marked_pending()
    {
        // **FR-020 applies to any surface showing the line, and this listing is one.** 020's quickstart
        // walk found it printing the live line with nothing said about a line already accepted — and
        // this is the listing somebody runs to check that an acceptance worked, so what it showed them
        // was the old line and no reason to think anything had happened.
        //
        // Must fail against reading the registry off the wire directly, which is what it did: the
        // comparison lives in `AgentRecord.OwnsPending` and the sentence in `Roster.PendingMark`, and
        // neither is reachable from a `RegistryRow`.
        await Register(Billing, "Invoicing and tax.");

        var (recorded, _) = await _agents.RecordOwnershipAsync(
            Billing,
            new OwnershipAcceptance("Invoicing, tax and refunds.", OwnershipSource.Composed, null, null),
            DateTimeOffset.UtcNow,
            Ct);

        Assert.True(recorded);

        await _commands.RegistryAsync(Ct);

        var printed = _output.ToString();

        // The live line, because that is what peers route by -- showing the accepted one would tell a
        // reader that peers read a sentence they have never been given.
        Assert.Contains("Invoicing and tax.", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("refunds", printed, StringComparison.Ordinal);

        // And the one sentence the other two surfaces append, not a wording of its own.
        Assert.Contains(
            Parley.Client.Rendering.Roster.PendingMark(
                new AgentRecord(
                    Billing, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active,
                    "Invoicing and tax.", AgentPresence.Startable, "Invoicing, tax and refunds.")),
            printed,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_whose_accepted_line_is_already_live_is_not_marked_pending()
    {
        // The mark clears itself when the agent comes back carrying the line, with no event and no
        // bookkeeping. Must fail against a mark driven by the row existing rather than by the
        // comparison, which is the obvious implementation and the wrong one.
        await Register(Orders, "Orders and refunds.");

        var (recorded, _) = await _agents.RecordOwnershipAsync(
            Orders,
            new OwnershipAcceptance("Orders and refunds.", OwnershipSource.Composed, null, null),
            DateTimeOffset.UtcNow,
            Ct);

        Assert.True(recorded);

        await _commands.RegistryAsync(Ct);

        Assert.DoesNotContain(
            "peers do not read it yet", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_operator_sees_the_statement_and_the_workspace_together()
    {
        // The two halves of telling one agent from another: where it works, which no peer sees,
        // and what it claims, which every peer does.
        await Register(Billing, "Invoicing and tax.");

        await _commands.RegistryAsync(Ct);

        var printed = _output.ToString();

        Assert.Contains("/src/acme-billing", printed, StringComparison.Ordinal);
        Assert.Contains("Invoicing and tax.", printed, StringComparison.Ordinal);
    }
}
