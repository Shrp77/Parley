using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// That a name with a line break after it is not a name.
/// </summary>
/// <remarks>
/// In .NET a pattern's <c>$</c> also matches
/// just before a final line feed, so a grammar anchored with it admits <c>orders@host</c> followed by one — a name that
/// prints as two lines wherever it is shown, which is the forgery the transcript's column rule and every listing guard
/// against.
/// </remarks>
[Unit]
public sealed class NamesEndWhereTheyEndTests
{
    [Theory]
    [InlineData("orders@host\n")]
    [InlineData("human-user@alpha\n")]
    public void An_agent_name_with_a_line_break_after_it_is_refused(string value) =>
        Assert.False(AgentName.TryCreate(value, out _));

    [Fact]
    public void A_project_name_with_a_line_break_after_it_is_refused() =>
        Assert.False(ProjectName.TryCreate("orders\n", out _));
}
