using System.ComponentModel;
using System.Reflection;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// An agent is told to keep a message in the conversation of the work that prompted it (SPEC.md §5.0). The broker cannot
/// tell what prompted a message, so these words are the whole of the rule.
/// </summary>
[Unit]
public sealed class AMessageStaysWithItsWorkTests
{
    private static string ClarifyDescription =>
        typeof(ParleyTools).GetMethod(nameof(ParleyTools.ClarifyAsync))!.GetCustomAttribute<DescriptionAttribute>()!.Description;

    [Fact]
    public void The_instructions_say_a_clarify_is_only_for_what_its_exchange_asked_and_to_send_a_request_before_the_result()
    {
        Assert.Contains("use it only for what that exchange asked", ParleyInstructions.Text, StringComparison.Ordinal);
        Assert.Contains("send a parley_request instead, before your parley_result", ParleyInstructions.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tool_says_so_where_the_choice_is_made()
    {
        Assert.Contains("Only for what this exchange asked", ClarifyDescription, StringComparison.Ordinal);
        Assert.Contains("even to this same agent", ClarifyDescription, StringComparison.Ordinal);
    }
}
