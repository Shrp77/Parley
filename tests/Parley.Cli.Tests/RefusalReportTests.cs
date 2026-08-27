using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What an operator sees when they ask what the size cap refused.
/// </summary>
/// <remarks>
/// The command exists so the next change to the cap can cite something. That makes its output
/// the deliverable: a count nobody can read, or one that cannot tell a cap set slightly low from
/// somebody trying to send a file, would leave the number being argued from opinion exactly as
/// before.
/// </remarks>
public sealed class RefusalReportTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly SizeRefusalRepository _refusals;
    private readonly StringWriter _output = new();
    private readonly LoopbackOperator _broker;
    private readonly OperatorCommands _commands;

    public RefusalReportTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _refusals = new SizeRefusalRepository(_connection);
        _broker = LoopbackOperator.Start(_connection);
        _commands = new OperatorCommands(_broker.Operators, _output);

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The broker first: it borrowed this connection and its shutdown writes to it.
        await _broker.DisposeAsync();

        _connection.Dispose();
        _output.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Printed => _output.ToString();

    [Fact]
    public async Task An_empty_record_says_so_and_says_what_the_cap_is()
    {
        // The ordinary state of a healthy deployment, and the one where somebody is most likely
        // to be looking because they want the cap changed. "Nothing" is an answer to that, and
        // an empty table printed as a header with no rows is not.
        Assert.Equal(0, await _commands.RefusalsAsync(Ct));

        Assert.Contains("Nothing has been refused", Printed, StringComparison.Ordinal);
        Assert.Contains(
            MessageLimits.MaxPayloadCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Printed,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_report_carries_what_a_decision_needs()
    {
        await _refusals.RecordAsync(Orders, MessageType.Answer, 4100, 4000, T0, Ct);
        await _refusals.RecordAsync(Orders, MessageType.Answer, 4300, 4000, T0.AddMinutes(1), Ct);
        await _refusals.RecordAsync(Billing, MessageType.Question, 40000, 4000, T0.AddMinutes(2), Ct);

        Assert.Equal(0, await _commands.RefusalsAsync(Ct));

        var printed = Printed;

        // Both agents, both types, the counts, the worst cases, and the mean that separates
        // them: 4,200 for the pair just over the line, 40,000 for the one sending a file.
        Assert.Contains("orders@test", printed, StringComparison.Ordinal);
        Assert.Contains("billing@test", printed, StringComparison.Ordinal);
        Assert.Contains("answer", printed, StringComparison.Ordinal);
        Assert.Contains("question", printed, StringComparison.Ordinal);
        Assert.Contains("4300", printed, StringComparison.Ordinal);
        Assert.Contains("4200", printed, StringComparison.Ordinal);
        Assert.Contains("40000", printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cap_in_force_at_the_time_is_reported_beside_the_measurement()
    {
        // Without it a row recorded under one cap is unreadable after the cap moves, which is
        // exactly when somebody goes looking at this table.
        await _refusals.RecordAsync(Orders, MessageType.Answer, 2500, 2000, T0, Ct);

        await _commands.RefusalsAsync(Ct);

        Assert.Contains("2000", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_refused_text_can_appear_because_none_is_kept()
    {
        // The report cannot leak what was refused, and not because it is careful: there is
        // nowhere for the payload to have been stored.
        await _refusals.RecordAsync(Orders, MessageType.Answer, 9000, 4000, T0, Ct);

        await _commands.RefusalsAsync(Ct);

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pragma_table_info('size_refusals') " +
                              "WHERE type = 'TEXT' AND name NOT IN ('agent', 'type', 'last_utc');";

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(Ct))!);
    }
}
