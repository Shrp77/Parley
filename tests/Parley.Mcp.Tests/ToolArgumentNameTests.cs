using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// A tool's arguments are spelled the way its replies, its prose and SPEC.md section 10.1 spell them, and a name it
/// does not declare is refused.
/// </summary>
/// <remarks>
/// These tests exist because every other test here calls <see cref="ParleyTools"/> as C# methods, so none of them could
/// see that the names an agent was told to use were not the names the schema took.
/// </remarks>
[Unit]
public sealed class ToolArgumentNameTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{"type":"object","properties":{"to":{},"conversation_id":{}}}""").RootElement;

    private static KeyValuePair<string, JsonElement> Named(string name) => new(name, default);

    [Fact]
    public void Every_argument_of_every_tool_is_spelled_in_snake_case()
    {
        var offenders = typeof(ParleyTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(m => m.GetParameters()
                .Where(p => p.ParameterType != typeof(CancellationToken) && p.Name!.Any(char.IsAsciiLetterUpper))
                .Select(p => $"{m.Name}({p.Name})"))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_declared_name_is_let_through()
    {
        Assert.Null(UnknownArguments.RefusalFor("parley_request", Schema, [Named("to"), Named("conversation_id")]));
        Assert.Null(UnknownArguments.RefusalFor("parley_request", Schema, null));
    }

    [Fact]
    public void An_undeclared_name_is_refused_saying_what_the_tool_takes()
    {
        var refusal = UnknownArguments.RefusalFor("parley_request", Schema, [Named("to"), Named("conversationId")])!;

        Assert.False(refusal["ok"]!.GetValue<bool>());
        Assert.Equal("malformed", refusal["refused"]!.GetValue<string>());

        var why = refusal["why"]!.GetValue<string>();

        Assert.Contains("parley_request has no argument named conversationId", why, StringComparison.Ordinal);
        Assert.Contains("only to, conversation_id", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_inside_an_item_is_held_to_the_schema_too()
    {
        var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"text":{},"needs":{}}}}}}""").RootElement;

        KeyValuePair<string, JsonElement> Items(string json) => new("items", JsonDocument.Parse(json).RootElement);

        Assert.Null(UnknownArguments.RefusalFor("parley_propose_roadmap", schema, [Items("""[{"text":"a"},{"text":"b","needs":[1]}]""")]));

        var why = UnknownArguments.RefusalFor(
            "parley_propose_roadmap", schema, [Items("""[{"text":"a"},{"text":"b","after":[1]}]""")])!["why"]!.GetValue<string>();

        Assert.Contains("In items[2], parley_propose_roadmap has no field named after", why, StringComparison.Ordinal);
        Assert.Contains("only text, needs", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_inside_a_declared_object_is_held_to_the_schema_too()
    {
        var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"outer":{"type":"object","properties":{"inner":{"type":"object","properties":{"kept":{}}}}}}}""").RootElement;

        var outer = new KeyValuePair<string, JsonElement>(
            "outer", JsonDocument.Parse("""{"inner":{"kept":1,"lost":2}}""").RootElement);

        var why = UnknownArguments.RefusalFor("parley_x", schema, [outer])!["why"]!.GetValue<string>();

        Assert.Contains("In outer.inner, parley_x has no field named lost", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_that_takes_nothing_says_so()
    {
        var empty = JsonDocument.Parse("""{"type":"object"}""").RootElement;

        var why = UnknownArguments.RefusalFor("parley_agents", empty, [Named("role")])!["why"]!.GetValue<string>();

        Assert.Contains("It takes no arguments", why, StringComparison.Ordinal);
    }
}
