using Parley.Client.Views;
using Parley.Core;
using Parley.Web.Pages.Projects;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the health page says what a permission-refused flag means and what to do (SPEC.md §6, specs/024).
/// </summary>
/// <remarks>
/// Every flag kind this page did not name fell through to "is retired and still holds it" — so the flag added for an agent
/// stopped by a refused command would have told the person the agent had been retired. The sentence is the one the
/// terminal's Health uses, from <see cref="Activities.RefusedFlag"/>, so the two cannot disagree.
/// </remarks>
public sealed class PermissionRefusedOnTheHealthPageTests
{
    [Fact]
    public void The_flag_is_described_as_a_refusal_with_its_remedy_and_never_as_a_retirement()
    {
        var flag = new Flag(
            FlagKind.PermissionRefused,
            7,
            "room",
            AgentName.Create("billing@here"),
            AgentName.Create("orders@here"),
            new DateTimeOffset(2026, 10, 3, 9, 5, 0, TimeSpan.Zero));

        var said = HealthModel.Describe(flag);

        Assert.Contains("refused for want of permission", said, StringComparison.Ordinal);
        Assert.Contains("Allow it in that repository's settings", said, StringComparison.Ordinal);
        Assert.DoesNotContain("retired", said, StringComparison.Ordinal);
    }
}
