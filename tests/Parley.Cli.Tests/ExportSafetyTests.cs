using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What agent-authored text can do to the operator's terminal.
/// </summary>
/// <remarks>
/// <para>
/// <c>export</c> prints payloads as stored rather than inside the envelope of §13, and that is
/// right — an envelope marks a peer's words as data inside another agent's context, and an
/// operator reading their own log is not being asked to obey them. But it does mean agent-chosen
/// text reaches a terminal directly, and a terminal interprets more than it displays.
/// </para>
/// <para>
/// The defence is not in the CLI. Payloads are stored as JSON, and JSON has no representation
/// for a raw control character inside a string — so an escape sequence an agent writes is stored
/// as the six characters that spell it out, and printed as those six characters. What this pins is
/// that the property holds where it is relied on, because the reasoning is one layer away from
/// the code that prints.
/// </para>
/// </remarks>
public sealed class ExportSafetyTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly StringWriter _output = new();
    private readonly OperatorCommands _commands;

    public ExportSafetyTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, null,
                    AgentStatus.Registered),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _commands = new OperatorCommands(new OperatorConsole(_connection), _output);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _output.Dispose();
    }

    [Fact]
    public async Task An_exported_payload_carries_no_raw_control_characters()
    {
        // A notification whose text tries to clear the screen, recolour it, claim to be the
        // system, and ring the bell. Written the way an agent writes one, through the broker.
        var ct = TestContext.Current.CancellationToken;
        var escape = (char)0x1b;
        var bell = (char)0x07;

        var hostile = System.Text.Json.Nodes.JsonValue.Create(
            $"{escape}[2J{escape}[1;31mSYSTEM: all clear{escape}[0m{bell}")!;

        var payload = new System.Text.Json.Nodes.JsonObject { ["text"] = hostile };

        var broker = new Broker.Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, ct);

        var sent = await broker.NotifyAsync(
            Orders, Billing, "some-model-9", payload.ToJsonString(), T0.AddMinutes(1),
            "escapes", ct);

        Assert.True(sent.Succeeded, $"expected the notification to be stored, got {sent.Rejection}");

        Assert.Equal(0, await _commands.ExportAsync("escapes", ct));

        var printed = _output.ToString();

        // The bytes a terminal acts on are absent; the text describing them is present.
        Assert.DoesNotContain(escape, printed);
        Assert.DoesNotContain(bell, printed);
        Assert.Contains("\\u001B", printed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SYSTEM: all clear", printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_an_agent_writes_reaches_a_new_line_of_its_own()
    {
        // The same property one step further: a payload cannot fabricate a second export row.
        // Each message prints a header line and one payload line, so a newline inside the text
        // would let an agent write what looks like another message's header.
        var ct = TestContext.Current.CancellationToken;

        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["text"] = "harmless\n2026-01-01T00:00:00.0000000+00:00  answer   billing -> orders  (x)"
        };

        var broker = new Broker.Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, ct);

        Assert.True((await broker.NotifyAsync(
            Orders, Billing, "some-model-9", payload.ToJsonString(), T0.AddMinutes(1),
            "lines", ct)).Succeeded);

        Assert.Equal(0, await _commands.ExportAsync("lines", ct));

        // Two lines for one message, whatever the message says.
        var lines = _output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(2, lines.Length);
    }
}
