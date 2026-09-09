using System.Reflection;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That nothing an agent can reach knows an instruction exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>SC-006, checked mechanically rather than by inspection.</b> SPEC.md §10.2 opens by warning
/// that an adapter surfacing an operator operation into an agent's tool set would breach §10.1, and
/// names <c>instruct</c> as the one operation that puts a person's words into an agent's turn. The
/// readback is the same surface read the other way: an agent able to list instructions could learn
/// what the operator had told a peer to do.
/// </para>
/// <para>
/// The wire split makes it structural — <c>Instructions</c> is on <c>Operators</c> and there is no
/// method on <c>Agents</c> that reads one. What this adds is the check one level closer to the
/// model: the tool set an MCP client is handed, by name, so a tool that acquired the word cannot
/// arrive quietly.
/// </para>
/// </remarks>
public sealed class InstructionReachTests
{
    [Fact]
    public void No_tool_an_agent_is_given_names_an_instruction_operation()
    {
        // By tool name rather than by reading the file, because the failure worth catching is a
        // tool added a year from now by somebody who did not read this.
        var tools = typeof(ParleyTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(name => name.Contains("Instruct", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            tools.Count == 0,
            "an agent-facing tool names an instruction operation: " + string.Join(", ", tools));
    }

    [Fact]
    public void The_adapter_carries_no_reference_to_the_broker_or_its_store()
    {
        // The structural half. §4 gives the store to one process and §10.2 to one surface; an
        // adapter that referenced either could bind an operator operation whatever its tools are
        // called.
        var referenced = typeof(ParleyTools).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain("Parley.Broker", referenced);
        Assert.DoesNotContain("Parley.Store", referenced);
    }

    [Fact]
    public void The_agent_wire_service_has_no_method_that_reads_an_instruction()
    {
        // Where the guarantee actually lives. An adapter binds Agents, and Agents must have nothing
        // that issues, settles or lists an instruction -- so no amount of confusion about which
        // client is in hand can produce a call that does.
        var onAgents = typeof(Parley.Transport.Agents.AgentsClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(name => name.Contains("Instruct", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            onAgents.Count == 0,
            "the agent service carries an instruction method: " + string.Join(", ", onAgents));
    }

    [Fact]
    public void The_operator_wire_service_does_carry_them()
    {
        // Non-vacuity for the three above. If the names were spelled differently from what those
        // tests search for, every one of them would pass by finding nothing anywhere.
        var onOperators = typeof(Parley.Transport.Operators.OperatorsClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(name => name.Contains("Instruct", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(onOperators);
    }
}
