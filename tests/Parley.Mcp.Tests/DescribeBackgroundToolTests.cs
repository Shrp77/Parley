using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>What the tool an agent describes its background work with says and does (SPEC.md §10.1, §11.6).</summary>
/// <remarks>
/// <para>
/// The person asked that a task's words be short, to the point and in their own terms, with a link to more. Parley bounds
/// the words and cannot judge them, so the guidance lives in the tool's description, which a model reads on every call,
/// and is held to it here.
/// </para>
/// <para>
/// <b>The answer carries no turn fact.</b> It says what became of the words, never whether the task runs.
/// </para>
/// </remarks>
[Unit]
public sealed class DescribeBackgroundToolTests
{
    private static MethodInfo Tool() =>
        typeof(ParleyTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "parley_describe_background");

    [Fact]
    public void The_description_says_how_to_word_it_and_who_sees_it()
    {
        var text = Tool().GetCustomAttribute<DescriptionAttribute>()!.Description;

        Assert.Contains("verb first", text, StringComparison.Ordinal);
        Assert.Contains("in the person's terms", text, StringComparison.Ordinal);
        Assert.Contains("at most 64 characters", text, StringComparison.Ordinal);
        Assert.Contains("at most 300 characters", text, StringComparison.Ordinal);
        Assert.Contains("No\n        background, no narrative", text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("exchange #N", text, StringComparison.Ordinal);
        Assert.Contains("Only the person sees what you write here", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tool_takes_the_task_and_its_words_and_no_sender()
    {
        var parameters = Tool().GetParameters().Select(p => p.Name!).Where(n => n != "cancellationToken").ToArray();

        Assert.Equal(["task_id", "name", "description", "link"], parameters);
    }

    [Fact]
    public void The_standing_instructions_tell_every_agent_about_it()
    {
        Assert.Contains("parley_describe_background", ParleyInstructions.Text, StringComparison.Ordinal);
    }
}

/// <summary>The tool through a real broker: what is kept and what is said back.</summary>
[Integration]
public sealed class DescribeBackgroundThroughTheBrokerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Words_are_kept_and_what_was_cut_or_dropped_is_said()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, tools) = await fixture.LaunchAsync("orders@test", ct);

        var plain = await tools.DescribeBackgroundAsync("a1", "Checking VAT on gift cards", "Reads the rules.", "exchange #5", ct);

        Assert.Equal(["ok", "kept"], plain.AsObject().Select(p => p.Key));
        Assert.True(plain["ok"]!.GetValue<bool>());
        Assert.True(plain["kept"]!.GetValue<bool>());

        var long_ = await tools.DescribeBackgroundAsync(
            "a2", new string('n', 100), new string('d', 400), "http://example.com/", ct);

        Assert.Equal(["ok", "kept", "name_cut", "description_cut", "link_dropped"], long_.AsObject().Select(p => p.Key));
        Assert.True(long_["name_cut"]!.GetValue<bool>());
        Assert.True(long_["description_cut"]!.GetValue<bool>());
        Assert.Contains("named host", long_["link_dropped"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_id_or_no_name_is_refused_with_why()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, tools) = await fixture.LaunchAsync("orders@test", ct);

        var badId = await tools.DescribeBackgroundAsync("not an id", "Checking VAT", cancellationToken: ct);
        var noName = await tools.DescribeBackgroundAsync("a1", "  ", cancellationToken: ct);

        Assert.Equal(["ok", "refused", "why"], badId.AsObject().Select(p => p.Key));
        Assert.False(badId["ok"]!.GetValue<bool>());
        Assert.Equal("notkept", badId["refused"]!.GetValue<string>());
        // The broker's own sentence follows, so the agent is told which of its words to change.
        Assert.Equal(
            "Nothing was kept. The task id is not one the runner gives: 1 to 64 letters, digits, hyphens and underscores, as the tool's result says it.",
            badId["why"]!.GetValue<string>());
        Assert.Equal(["ok", "refused", "why"], noName.AsObject().Select(p => p.Key));
        Assert.False(noName["ok"]!.GetValue<bool>());
        Assert.Equal("notkept", noName["refused"]!.GetValue<string>());
        Assert.Equal(
            "Nothing was kept. A name is needed: what the task is doing, in a few words.",
            noName["why"]!.GetValue<string>());
    }
}
