using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What an agent can actually call.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to the broker's own surface test, one layer out and for the same reason. SPEC.md §10.2 warns
/// that an adapter surfacing an operator operation into an agent's tool set would breach §10.1, and the protection
/// on offer is a naming convention — <c>force_conclude</c> is deliberately not <c>conclude</c>. That is thin, so
/// the structural guard is that an adapter holds a client for the <c>Agents</c> service only.
/// </para>
/// <para>
/// This pins what the tool set actually contains, so adding a tool is a decision about which side of §10.1 it
/// belongs on rather than a question of which class the method was already in.
/// </para>
/// </remarks>
public sealed class ToolSurfaceTests
{
    private static readonly string[] Expected =
    [
        "parley_agents",
        "parley_clarify",
        "parley_conclude",
        "parley_inbox",
        "parley_request",
        "parley_result"
    ];

    private static IEnumerable<MethodInfo> ToolMethods() =>
        typeof(ParleyTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    [Fact]
    public void The_agent_facing_tool_set_is_exactly_the_six_operations_of_section_10_1()
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
        // §13's guarantee, checked structurally rather than trusted. Identity comes from the launch environment,
        // and the reason no tool call can change who the caller is, is that no tool call mentions it.
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
    public void Every_refusal_code_is_spelled_the_same_way()
    {
        // The refusal code is what a model matches on to decide what to do next. Two spellings of one condition
        // fail in the worst way, so the shape is settled: lowercase letters, no separators.
        var source = File.ReadAllText(SourcePath("ParleyTools.cs"));
        var literals = System.Text.RegularExpressions.Regex
            .Matches(source, @"\[""refused""\] = ""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(literals);
        Assert.All(literals, code => Assert.Matches("^[a-z]+$", code));
    }

    [Fact]
    public void What_the_tools_promise_matches_what_they_accept()
    {
        // §11.4 puts the rules of §8 in tool descriptions because that is what an agent re-reads on every call —
        // which makes a description that has drifted from the code worse than a stale doc comment.
        var source = File.ReadAllText(SourcePath("ParleyTools.cs"));
        var descriptions = System.Text.RegularExpressions.Regex
            .Matches(source, @"\[Description\((.*?)\)\]", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToArray();

        // The Result tool names every blocker kind the broker accepts, and no outcome it refuses.
        var result = Assert.Single(descriptions, d => d.Contains("Report what a request came to", StringComparison.Ordinal));
        Assert.Contains("missing_information", result, StringComparison.Ordinal);
        Assert.Contains("conflict", result, StringComparison.Ordinal);
        Assert.Contains("permission", result, StringComparison.Ordinal);
        Assert.DoesNotContain("out_of_scope", result, StringComparison.Ordinal);

        // A conversation id reaches a filename, so every description offering one says what it may contain.
        Assert.All(
            descriptions.Where(d => d.Contains("conversation this belongs to", StringComparison.Ordinal)),
            d => Assert.Contains("Letters, digits", d, StringComparison.Ordinal));
    }

    [Fact]
    public void No_description_tells_an_agent_to_decline_work_it_was_asked_for()
    {
        // The descriptions this rewrite replaced told agents to answer "out of scope" and stop, and that a
        // participant's message could not direct their work. Agents followed them and declined the work they were
        // asked for. A sentence an agent could read as a reason not to do requested work is a defect (§8). The
        // descriptions only: the type's own remarks quote the old wording to say why it went.
        var source = File.ReadAllText(SourcePath("ParleyTools.cs"));
        var descriptions = System.Text.RegularExpressions.Regex
            .Matches(source, @"\[Description\((.*?)\)\]", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(descriptions);

        foreach (var phrase in new[] { "out of scope", "out_of_scope", "not your work", "cannot direct" })
        {
            Assert.All(descriptions, d => Assert.DoesNotContain(phrase, d, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Every_tool_carries_a_description_because_that_is_where_section_8_lives()
    {
        var undescribed = ToolMethods()
            .Where(m => m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() is null)
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(undescribed);
    }

    /// <summary>Locates a source file in the adapter project.</summary>
    /// <param name="fileName">The file to find.</param>
    /// <returns>Its full path.</returns>
    /// <remarks>
    /// Reading the source is unusual and is the honest way to check this one. The codes are string literals chosen
    /// at each call site, so nothing at runtime enumerates them.
    /// </remarks>
    private static string SourcePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory.FullName, "src", "Parley.Mcp", fileName);
    }
}
