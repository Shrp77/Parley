using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>A broker shown by address is a window, and its name is no project's (SPEC.md §4.1.2).</summary>
[Unit]
public sealed class AWindowEnrolsNothingTests
{
    [Fact]
    public void A_broker_shown_by_address_is_a_window()
    {
        var window = new ProjectSettings(
            ProjectName.Create(ProjectManifest.WindowName), string.Empty, "http://127.0.0.1:19100", null);

        Assert.True(Onboarding.IsAWindow(window));
    }

    [Fact]
    public void A_project_is_not_and_nor_is_having_none()
    {
        var project = new ProjectSettings(
            ProjectName.Create("alpha"), "alpha.db", "http://127.0.0.1:19100", null);

        Assert.False(Onboarding.IsAWindow(project));
        Assert.False(Onboarding.IsAWindow(null));
    }

    [Fact]
    public void It_says_how_to_enrol_instead()
    {
        Assert.Contains("parley enroll", Onboarding.AWindowEnrolsNothing, StringComparison.Ordinal);
        Assert.Contains("--project", Onboarding.AWindowEnrolsNothing, StringComparison.Ordinal);
        Assert.Contains("--broker", Onboarding.AWindowEnrolsNothing, StringComparison.Ordinal);
    }
}
