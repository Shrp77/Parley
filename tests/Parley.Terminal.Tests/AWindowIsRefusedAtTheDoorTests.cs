using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>A broker shown by address is a window, and each way of enrolling says so before asking anything (SPEC.md §4.1.2).</summary>
/// <remarks>
/// The arguments a real opening needs are left null on purpose: a door that reached for any of them has gone past the refusal.
/// </remarks>
[Unit]
public sealed class AWindowIsRefusedAtTheDoorTests
{
    private static readonly ProjectSettings Window =
        new(ProjectName.Create(ProjectManifest.WindowName), string.Empty, "http://127.0.0.1:1", null);

    [Fact]
    public async Task Adding_an_agent_says_so_before_the_folder_walk()
    {
        string? said = null;

        await Adding.OpenAsync(
            null!, Window, null!, null, null, null!, text =>
            {
                said = text;

                return Task.CompletedTask;
            });

        Assert.Equal(EnrollmentPlan.AWindowEnrolsNothing, said);
    }

    [Fact]
    public async Task Enrolling_from_folders_says_so_before_the_folder_walk()
    {
        string? said = null;

        await Onboarding.OpenAsync(
            null!, Window, [], null, null, null, null!, null!, text =>
            {
                said = text;

                return Task.CompletedTask;
            });

        Assert.Equal(EnrollmentPlan.AWindowEnrolsNothing, said);
    }
}
