using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Parley.Mcp;

/// <summary>
/// Refuses a tool call that names an argument, or a field inside one, the tool does not declare (SPEC.md section 10.1).
/// </summary>
/// <remarks>
/// The SDK binds the names it knows and drops the rest without a word. For an optional name that is the worst outcome
/// on offer: the call succeeds without what its caller asked for, so a Request naming a conversation under a misspelt
/// key is filed in another one, and a roadmap item's <c>after</c> loses what it depends on, and nothing says so. It runs
/// ahead of the tool, so a refused call changes nothing, and it reads the names from the schema the caller was shown,
/// so the two cannot disagree.
/// </remarks>
public static class UnknownArguments
{
    /// <summary>Wraps the call-tool pipeline with the refusal.</summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>The wrapped handler.</returns>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Refuse(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, cancellationToken) =>
        {
            var refusal = context.MatchedPrimitive is McpServerTool tool
                ? RefusalFor(tool.ProtocolTool.Name, tool.ProtocolTool.InputSchema, context.Params?.Arguments)
                : null;

            return refusal is null
                ? next(context, cancellationToken)
                : ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = refusal.ToJsonString() }]
                });
        };

    /// <summary>Works out whether a call names anything its tool does not declare.</summary>
    /// <param name="tool">The tool's name.</param>
    /// <param name="schema">The tool's input schema.</param>
    /// <param name="arguments">The arguments the call named, if any.</param>
    /// <returns>A refusal in the tools' own shape, or <see langword="null"/> when every name is declared.</returns>
    public static JsonNode? RefusalFor(
        string tool, JsonElement schema, IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        var sentences = new List<string>();

        var unknown = new List<string>();
        foreach (var (name, value) in arguments ?? [])
        {
            if (TryDeclared(schema, name, out var declared))
            {
                Walk(tool, declared, value, name, sentences);
            }
            else
            {
                unknown.Add(name);
            }
        }

        if (unknown.Count > 0)
        {
            sentences.Insert(0, $"{tool} has no argument named {string.Join(", ", unknown)}. It takes {Takes(schema)}.");
        }

        if (sentences.Count == 0)
        {
            return null;
        }

        return new JsonObject
        {
            ["ok"] = false,
            ["refused"] = "malformed",
            ["why"] = $"{string.Join(" ", sentences)} Nothing was done."
        };
    }

    /// <summary>Looks inside one value for fields its schema does not declare.</summary>
    /// <param name="tool">The tool's name.</param>
    /// <param name="schema">The value's schema.</param>
    /// <param name="value">The value.</param>
    /// <param name="path">Where the value is, as the caller would say it.</param>
    /// <param name="sentences">Where a refusal's sentence is added.</param>
    private static void Walk(string tool, JsonElement schema, JsonElement value, string path, List<string> sentences)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var position = 0;
            foreach (var element in value.EnumerateArray())
            {
                position++;
                Walk(tool, items, element, $"{path}[{position}]", sentences);
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out _))
        {
            return;
        }

        var unknown = new List<string>();
        foreach (var member in value.EnumerateObject())
        {
            if (TryDeclared(schema, member.Name, out var declared))
            {
                Walk(tool, declared, member.Value, $"{path}.{member.Name}", sentences);
            }
            else
            {
                unknown.Add(member.Name);
            }
        }

        if (unknown.Count > 0)
        {
            sentences.Add($"In {path}, {tool} has no field named {string.Join(", ", unknown)}. It takes {Takes(schema)}.");
        }
    }

    private static bool TryDeclared(JsonElement schema, string name, out JsonElement declared)
    {
        declared = default;

        return schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(name, out declared);
    }

    private static string Takes(JsonElement schema)
    {
        var names = schema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(p => p.Name).ToArray()
            : [];

        return names.Length == 0 ? "no arguments" : $"only {string.Join(", ", names)}";
    }
}
