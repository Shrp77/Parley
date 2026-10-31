using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// What a surface says is wrong with a project's deployment beyond whether its broker answers.
/// </summary>
[Unit]
public sealed class ProjectHealthWarningsTests
{
    [Fact]
    public void A_broker_from_another_release_is_warned_about_with_both_numbers()
    {
        var health = new ProjectHealth("0.8", null, "0.41.5", []);

        var warning = Assert.Single(health.Warnings("0.42.1"));

        Assert.Contains("0.41.5", warning, StringComparison.Ordinal);
        Assert.Contains("0.42.1", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_patch_difference_is_not_warned_about()
    {
        Assert.Empty(new ProjectHealth("0.8", null, "0.42.0", []).Warnings("0.42.1"));
    }

    [Fact]
    public void A_store_of_the_wrong_shape_is_warned_about_and_names_what_is_missing()
    {
        // Must fail against warning only about versions: the numbers all agree.
        var health = new ProjectHealth("0.8", null, "0.42.1", ["missing table roadmap_notices"]);

        var warning = Assert.Single(health.Warnings("0.42.1"));

        Assert.Contains("missing table roadmap_notices", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broker_that_said_nothing_is_not_accused()
    {
        // An older broker cannot be asked, and "not said" is not "wrong". Must fail against treating empty as a mismatch.
        Assert.Empty(new ProjectHealth("0.8", null).Warnings("0.42.1"));
    }

    [Fact]
    public void A_broker_that_did_not_answer_has_nothing_to_compare()
    {
        Assert.Empty(new ProjectHealth(null, "connection refused", "0.41.5", ["missing table x"]).Warnings("0.42.1"));
    }

    [Fact]
    public void Both_problems_are_said_when_both_are_there()
    {
        Assert.Equal(2, new ProjectHealth("0.8", null, "0.41.5", ["missing table x"]).Warnings("0.42.1").Count);
    }
}
