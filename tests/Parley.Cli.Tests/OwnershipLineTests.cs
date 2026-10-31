using System.Text;
using System.Text.Json.Nodes;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Writing an accepted ownership line into a repository's launch configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only thing that makes an accepted line reach anybody.</b> The registry's
/// <c>owns</c> is filled from the launch environment on attach and no operation writes it, so
/// <c>record_ownership</c> stores a decision and this puts it where the decision takes effect. A
/// feature with one half and not the other records agreements that can never become true.
/// </para>
/// <para>
/// The refusals are the probes: every one of them is a case where
/// the tempting implementation writes something.
/// </para>
/// </remarks>
[Integration]
public sealed class OwnershipLineTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-owns-" + Path.GetRandomFileName());

    public OwnershipLineTests() => Directory.CreateDirectory(Workspace);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private string Workspace => Path.Combine(_root, "orders");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    /// <summary>The agent this machine can actually write for.</summary>
    /// <remarks>
    /// Built from <see cref="MachineName.Current"/> rather than from a literal, because the machine
    /// check compares against exactly that — a fixture naming <c>@test</c> would be refused before it
    /// reached the thing under test, and would have looked like the write failing.
    /// </remarks>
    private static AgentName Here(string role = "orders") =>
        AgentName.Create($"{role}@{MachineName.Current}");

    /// <summary>Writes a launch configuration naming <paramref name="role"/> under the parley key.</summary>
    private void Launch(string role = "orders", bool mark = false)
    {
        var text =
            $$"""
              {
                "mcpServers": {
                  "parley": {
                    "command": "parley-mcp",
                    "env": { "PARLEY_AGENT": "{{role}}" }
                  }
                }
              }
              """;

        File.WriteAllText(LaunchFile, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: mark));
    }

    private string? Owns()
    {
        var root = JsonNode.Parse(File.ReadAllText(LaunchFile))!;

        return root["mcpServers"]!["parley"]!["env"]!["PARLEY_OWNS"]?.GetValue<string>();
    }

    [Fact]
    public async Task The_line_is_written_where_the_adapter_reads_it()
    {
        Launch();

        Assert.Null(await OwnershipLine.WriteAsync(
            Workspace, null, Here(), "Invoicing and tax. Not stock.", TestContext.Current.CancellationToken));

        Assert.Equal("Invoicing and tax. Not stock.", Owns());
    }

    [Fact]
    public async Task A_line_can_be_taken_back_out()
    {
        // Declining is not the same as writing an empty line, and an agent with no line
        // is listed without one. So null removes the variable rather than setting it to "".
        Launch();

        await OwnershipLine.WriteAsync(Workspace, null, Here(), "Invoicing.", TestContext.Current.CancellationToken);
        Assert.Null(await OwnershipLine.WriteAsync(
            Workspace, null, Here(), null, TestContext.Current.CancellationToken));

        Assert.Null(Owns());
        Assert.DoesNotContain("PARLEY_OWNS", await File.ReadAllTextAsync(
            LaunchFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_byte_order_mark_survives_the_write()
    {
        // The rule enrolment paid for once. A round trip that dropped it would rewrite a file
        // somebody's editor then reports as changed for reasons nothing explains.
        Launch(mark: true);

        await OwnershipLine.WriteAsync(
            Workspace, null, Here(), "Invoicing.", TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(LaunchFile, TestContext.Current.CancellationToken);

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    [Fact]
    public async Task An_entry_naming_a_different_agent_is_refused_and_nothing_is_written()
    {
        // **005's review-gate finding, reached from the other side.** A withdrawal once deleted a
        // launch configuration belonging to a different agent, because the workspace came from the
        // registry -- what the agent said about itself, possibly long out of date -- and a repository
        // re-enrolled under a second name has one file and two rows pointing at it.
        //
        // Must fail against trusting the workspace and writing: that is the whole of the defect.
        Launch("billing");

        var before = await File.ReadAllBytesAsync(LaunchFile, TestContext.Current.CancellationToken);

        var wrong = await OwnershipLine.WriteAsync(
            Workspace, null, Here(), "Invoicing.", TestContext.Current.CancellationToken);

        Assert.NotNull(wrong);
        Assert.Contains("billing", wrong, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(LaunchFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_line_naming_an_environment_variable_is_refused_and_nothing_is_written()
    {
        // Lens 3 run 112: every route a person takes reaches the file through here, so this guard holds them all.
        Launch();

        var wrong = await OwnershipLine.WriteAsync(
            Workspace, null, Here(), "Invoicing. ${GITHUB_TOKEN}", TestContext.Current.CancellationToken);

        Assert.NotNull(wrong);
        Assert.Contains("launch configuration", wrong, StringComparison.Ordinal);
        Assert.Null(Owns());
    }

    [Fact]
    public async Task An_agent_on_another_machine_is_refused_by_name()
    {
        // `research.md` R3: a launch configuration is a file on the agent's machine and neither
        // surface is guaranteed to be there. One rule for both surfaces, not a web-UI restriction.
        Launch();

        var wrong = await OwnershipLine.WriteAsync(
            Workspace, null, AgentName.Create("orders@server-b"), "Invoicing.",
            TestContext.Current.CancellationToken);

        Assert.NotNull(wrong);
        Assert.Contains("server-b", wrong, StringComparison.Ordinal);
        Assert.Null(Owns());
    }

    [Fact]
    public async Task A_repository_that_is_not_there_is_refused_naming_the_path()
    {
        var gone = Path.Combine(_root, "moved-away");

        var wrong = await OwnershipLine.WriteAsync(
            gone, null, Here(), "Invoicing.", TestContext.Current.CancellationToken);

        Assert.NotNull(wrong);
        Assert.Contains(gone, wrong, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_with_no_launch_configuration_is_refused_rather_than_given_one()
    {
        // Creating one here would enrol a repository as a side effect of somebody accepting a line,
        // which is a write into a checkout by a command that reported doing something else.
        var wrong = await OwnershipLine.WriteAsync(
            Workspace, null, Here(), "Invoicing.", TestContext.Current.CancellationToken);

        Assert.NotNull(wrong);
        Assert.False(File.Exists(LaunchFile));
    }

    [Fact]
    public async Task A_statement_the_registry_would_refuse_is_refused_before_the_file_is_opened()
    {
        // The character rule, checked here as well as in storage. A launch configuration carrying a
        // line the broker refuses produces an agent refused at registration -- a failure a long way
        // from the person who caused it, which reads as an agent that never appeared.
        Launch();

        var before = await File.ReadAllBytesAsync(LaunchFile, TestContext.Current.CancellationToken);

        var wrong = await OwnershipLine.WriteAsync(
            Workspace, null, Here(), "Invoicing." + (char)0x2028 + "Everything.",
            TestContext.Current.CancellationToken);

        Assert.NotNull(wrong);
        Assert.Equal(before, await File.ReadAllBytesAsync(LaunchFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_long_statement_is_written_because_the_bound_came_off()
    {
        Launch();

        Assert.Null(await OwnershipLine.WriteAsync(
            Workspace, null, Here(), new string('x', 1_200), TestContext.Current.CancellationToken));

        Assert.Equal(1_200, Owns()!.Length);
    }

    [Fact]
    public async Task An_unreadable_launch_configuration_is_refused_and_left_exactly_as_it_was()
    {
        // Must fail against an implementation that parses what it can and rewrites the rest, which
        // would destroy whatever somebody had in there.
        await File.WriteAllTextAsync(LaunchFile, "{ this is not json", TestContext.Current.CancellationToken);

        var before = await File.ReadAllBytesAsync(LaunchFile, TestContext.Current.CancellationToken);

        var wrong = await OwnershipLine.WriteAsync(
            Workspace, null, Here(), "Invoicing.", TestContext.Current.CancellationToken);

        Assert.NotNull(wrong);
        Assert.Equal(before, await File.ReadAllBytesAsync(LaunchFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Everything_else_in_the_file_survives()
    {
        // A launch configuration holds other people's servers. A write that dropped them would be a
        // tool breaking an editor's configuration to record one line.
        await File.WriteAllTextAsync(
            LaunchFile,
            """
            {
              "mcpServers": {
                "something-else": { "command": "other-tool" },
                "parley": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } }
              }
            }
            """,
            TestContext.Current.CancellationToken);

        Assert.Null(await OwnershipLine.WriteAsync(
            Workspace, null, Here(), "Invoicing.", TestContext.Current.CancellationToken));

        var root = JsonNode.Parse(await File.ReadAllTextAsync(
            LaunchFile, TestContext.Current.CancellationToken))!;

        Assert.Equal("other-tool", root["mcpServers"]!["something-else"]!["command"]!.GetValue<string>());
        Assert.Equal("orders", root["mcpServers"]!["parley"]!["env"]!["PARLEY_AGENT"]!.GetValue<string>());
        Assert.Equal("Invoicing.", Owns());
    }
}
