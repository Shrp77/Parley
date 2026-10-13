using Parley.Core;
using Parley.Web.Pages;
using Xunit;
using Parley.Client;

namespace Parley.Web.Tests;

/// <summary>
/// That a broker speaking a different version is named rather than refused or hidden.
/// </summary>
/// <remarks>
/// <para>
/// Three states, not two. A broker that is down and a broker that answers with a version this
/// console was not built against fail in the same visible way — an empty or partial page — and
/// the remedies are opposite: one is a process to start, the other is a build to reconcile.
/// Collapsing them costs somebody the afternoon.
/// </para>
/// <para>
/// A mismatch does not stop the console. What a version disagreement means for any particular
/// read is not knowable from the numbers, so the console says both and keeps working: refusing
/// would remove the view at the moment it is most needed.
/// </para>
/// </remarks>
public sealed class VersionTests
{
    [Fact]
    public void A_broker_speaking_this_version_agrees()
    {
        var health = new ProjectHealth(ProtocolVersion.Current, null);

        Assert.True(health.Reachable);
        Assert.True(health.Agrees);
    }

    [Fact]
    public void A_broker_speaking_another_version_is_reachable_and_does_not_agree()
    {
        // The state that must not collapse into either neighbour. It answered, so it is not down;
        // it answered with something else, so what its pages show cannot be taken at face value.
        var health = new ProjectHealth("99.9", null);

        Assert.True(health.Reachable);
        Assert.False(health.Agrees);
    }

    [Fact]
    public void A_broker_that_did_not_answer_is_neither_reachable_nor_agreeing()
    {
        var health = new ProjectHealth(null, "not answering");

        Assert.False(health.Reachable);
        Assert.False(health.Agrees);
    }

    [Fact]
    public void A_missing_version_from_a_reachable_broker_does_not_pass_as_agreement()
    {
        // The failure mode worth writing down: a broker that answers Implements with an empty
        // string. Comparing null to null, or empty to unset, would report agreement with
        // something that never stated a version at all.
        Assert.False(new ProjectHealth(null, null).Agrees);
        Assert.False(new ProjectHealth(string.Empty, null).Agrees);
    }

    [Fact]
    public void The_console_names_the_version_it_was_built_against()
    {
        // Both numbers or neither. "Incompatible" without them cannot be acted on, and the
        // console's own number is the half a reader cannot look up from the broker.
        Assert.Equal(ProtocolVersion.Current, IndexModel.ConsoleSpeaks);
    }

    [Fact]
    public void The_projects_page_says_the_pages_below_still_open()
    {
        // The behaviour, not the wording: a mismatch is a warning and not a gate. A console that
        // refused would take the view away at the moment it is most needed.
        var markup = File.ReadAllText(RepositoryPath("src/Parley.Web/Pages/Shared/_ProjectRow.cshtml"));

        Assert.Contains("The pages below still", markup, StringComparison.Ordinal);
        Assert.Contains(".Speaks", markup, StringComparison.Ordinal);
        Assert.Contains("ConsoleSpeaks", markup, StringComparison.Ordinal);
    }

    /// <summary>Finds a path in the repository from wherever the tests run.</summary>
    /// <param name="relative">The path, relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 }
               && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
