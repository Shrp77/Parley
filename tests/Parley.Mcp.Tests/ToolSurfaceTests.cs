using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What an agent can actually call.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to the broker's own surface test, one layer out and for the same reason.
/// SPEC.md §10.2 warns that an adapter surfacing an operator operation into an agent's tool set
/// would breach §10.1, and observes that the protection on offer is a naming convention —
/// <c>force_conclude</c> is deliberately not <c>conclude</c>. That is thin, so the structural
/// guard is that an adapter is handed a <c>Broker</c> and a <c>Broker</c> has no operator
/// operation to bind.
/// </para>
/// <para>
/// This is the last link in that chain: it pins what the tool set actually contains, so adding
/// a tool is a decision about which side of §10.1 it belongs on rather than a question of which
/// class the method was already in. A test that fails when the list grows is worth more than a
/// convention that asks the next author to have read this paragraph.
/// </para>
/// </remarks>
public sealed class ToolSurfaceTests
{
    private static readonly string[] Expected =
    [
        "parley_agents",
        "parley_answer",
        "parley_ask",
        "parley_clarify",
        "parley_conclude",
        "parley_inbox",
        "parley_notify"
    ];

    private static IEnumerable<MethodInfo> ToolMethods() =>
        typeof(ParleyTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    [Fact]
    public void The_agent_facing_tool_set_is_exactly_the_seven_operations_of_section_10_1()
    {
        var names = ToolMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Expected, names);
    }

    [Fact]
    public void No_tool_takes_the_acting_agent_as_a_parameter()
    {
        // §13's guarantee, checked structurally rather than trusted. Identity comes from the
        // launch environment, and the reason no tool call can change who the caller is, is that
        // no tool call mentions it. A parameter named for the sender or for an agent would put
        // that back in the model's hands however carefully its description was worded.
        var offenders = ToolMethods()
            .SelectMany(m => m.GetParameters().Select(p => $"{m.Name}({p.Name})"))
            .Where(signature =>
                signature.Contains("from", StringComparison.OrdinalIgnoreCase) ||
                signature.Contains("sender", StringComparison.OrdinalIgnoreCase) ||
                signature.Contains("actingAgent", StringComparison.OrdinalIgnoreCase) ||
                signature.Contains("asAgent", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_tool_carries_a_description_because_that_is_where_section_8_lives()
    {
        // §11.4: the rules of §8 live in tool descriptions, which Full and Pull-only agents
        // re-read on every call. A tool without one is a tool an agent uses without the rules.
        var undescribed = ToolMethods()
            .Where(m => m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() is null)
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(undescribed);
    }
}
