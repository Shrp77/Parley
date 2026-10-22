using System.Reflection;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That nothing an agent can reach knows an instruction exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Instruct was removed, and this keeps it from coming back by the agent's door.</b> A person
/// speaks to an agent only by sending a Request from the seat, which is a message in a conversation both can
/// read. A route that put a person's words into an agent's turn outside any conversation would be a second way
/// in that nobody could read afterwards.
/// </para>
/// <para>
/// The wire split makes it structural — there is no method on <c>Agents</c> that gives or reads an
/// instruction. What this adds is the check one level closer to the model: the tool set an MCP client is
/// handed, by name, so a tool that acquired the word cannot arrive quietly.
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
}
