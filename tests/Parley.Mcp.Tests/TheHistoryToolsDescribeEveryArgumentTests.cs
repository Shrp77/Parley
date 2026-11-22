using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That an agent is told what each argument of the history tools takes, and that an empty search with a filter says the
/// filter may be why.
/// </summary>
/// <remarks>
/// The XML <c>param</c> documentation never reaches an agent: only <c>[Description]</c> does. Without it a participant
/// given as a role, as <c>parley_request</c> takes <c>to</c>, matched nothing, and the reply told the agent to change its
/// words (review, lens 3 sweep B on 3b752970).
/// </remarks>
[Integration]
public sealed class TheHistoryToolsDescribeEveryArgumentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("SearchHistoryAsync")]
    [InlineData("ReadExchangeAsync")]
    public void Every_argument_an_agent_sees_is_described(string name)
    {
        var method = typeof(ParleyTools).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;
        var tool = McpServerTool.Create(method, _ => throw new InvalidOperationException("not called"));
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        Assert.NotEmpty(properties.EnumerateObject());

        foreach (var property in properties.EnumerateObject())
        {
            Assert.True(
                property.Value.TryGetProperty("description", out var description)
                    && description.ValueKind == JsonValueKind.String
                    && description.GetString()!.Length > 0,
                $"{name}'s argument {property.Name} is not described");
        }
    }

    [Fact]
    public async Task An_empty_search_with_a_filter_names_the_filters()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, docs) = await fixture.LaunchAsync("docs@test", ct);
        _ = await fixture.LaunchAsync("infra@alpha", ct);
        var (_, reader) = await fixture.LaunchAsync("billing@test", ct);

        _ = await docs.RequestAsync("infra@alpha", "What does this machine believe about DNS?", cancellationToken: ct);

        var byRole = (await reader.SearchHistoryAsync("DNS", participant: "docs", cancellationToken: ct)).ToJsonString();
        var unmatched = (await reader.SearchHistoryAsync("kerberos", cancellationToken: ct)).ToJsonString();

        Assert.Contains("filters", byRole, StringComparison.Ordinal);
        Assert.DoesNotContain("filters", unmatched, StringComparison.Ordinal);
    }
}
