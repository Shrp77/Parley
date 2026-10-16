using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What the tool an agent proposes a roadmap with says and does not do (SPEC.md §5.7, principle 2.12).
/// </summary>
/// <remarks>
/// The description is what a model reads on every call, so the template lives there and is held to it: factual, point
/// oriented and short, and clear that this only proposes. It is guidance and not a limit,
/// so nothing here asserts a length bound, and a test that did would be the quiet reversal of that decision.
/// </remarks>
public sealed class RoadmapToolTests
{
    private static MethodInfo Tool() =>
        typeof(ParleyTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "parley_propose_roadmap");

    private static string Description() => Tool().GetCustomAttribute<DescriptionAttribute>()!.Description;

    [Fact]
    public void The_description_carries_the_template_and_says_it_only_proposes()
    {
        var text = Description();

        Assert.Contains("title", text, StringComparison.Ordinal);
        Assert.Contains("verb first", text, StringComparison.Ordinal);
        Assert.Contains("No background, no reasons, no narrative", text, StringComparison.Ordinal);
        Assert.Contains("assigns nobody", text, StringComparison.Ordinal);
        Assert.Contains("until the person confirms", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tool_takes_no_sender_and_no_roadmap_to_change()
    {
        var parameters = Tool().GetParameters().Select(p => p.Name!).ToArray();

        Assert.DoesNotContain(parameters, p => p.Contains("agent", StringComparison.OrdinalIgnoreCase)
            || p.Contains("from", StringComparison.OrdinalIgnoreCase)
            || p.Contains("sender", StringComparison.OrdinalIgnoreCase));

        // A revision is named by title and only as a hint: an agent never reads a roadmap, so it has no identifier
        // to name one by.
        Assert.Contains("revises", parameters);
        Assert.DoesNotContain(parameters, p => p.Contains("id", StringComparison.OrdinalIgnoreCase)
            && !p.Equals("conversationId", StringComparison.Ordinal));
    }

    [Fact]
    public void An_item_has_three_fields_and_nowhere_to_put_a_paragraph()
    {
        var properties = typeof(RoadmapItemInput).GetProperties().Select(p => p.Name).Order().ToArray();

        Assert.Equal(["For", "Needs", "Text"], properties);
    }

    [Fact]
    public void The_standing_instructions_tell_every_agent_about_it_in_the_same_short_terms()
    {
        Assert.Contains("parley_propose_roadmap", ParleyInstructions.Text, StringComparison.Ordinal);
        Assert.Contains("verb first", ParleyInstructions.Text, StringComparison.Ordinal);
        Assert.Contains("the person decides", ParleyInstructions.Text, StringComparison.Ordinal);
    }
}
