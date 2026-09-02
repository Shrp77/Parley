using System.Runtime.CompilerServices;
using Parley.Configuration;
using Xunit;

namespace Parley.Tests;

/// <summary>
/// Points a test assembly away from the project manifest of the machine running it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by probing three assemblies, one at a time, after the first one failed.</b> Anything
/// that can reach <see cref="ProjectManifest.Locate"/> resolves the per-user default when nothing
/// says otherwise, and on a developer machine that has named a deployment the default is that
/// deployment. <c>Parley.Web.Tests</c> was the bad one: the bare
/// <c>WebApplicationFactory&lt;Program&gt;</c> builds the real console, so the test host listed a
/// live project and opened gRPC channels to the broker serving it. <c>Parley.Cli.Tests</c> and
/// <c>Parley.Mcp.Tests</c> resolved the same file in process.
/// </para>
/// <para>
/// Nothing wrote to a real deployment. The console's destructive pages act only on POST and every
/// test that drives them replaces the directory outright — but that is a containment holding, not
/// isolation working, and it is one forgetful test away from not holding.
/// </para>
/// <para>
/// <b>A path, not an empty value.</b> <see cref="ProjectManifest.Locate"/> reads an empty variable
/// as unset, and unset means the default — so clearing the variable does not avoid the machine's
/// manifest, it selects it. That inversion is what made the original guard in <c>ToolProcess</c>
/// do the opposite of its own comment, and it passed everywhere for a year because no machine
/// running the suite had named a project.
/// </para>
/// <para>
/// One file, linked into each assembly that references <c>Parley.Configuration</c>, rather than a
/// copy per project. <c>ToolProcess</c>'s own remarks record what happened last time this
/// repository kept two copies of an isolation helper: they had already begun to differ, and which
/// stray variable could reach a test depended on which file the test was in.
/// </para>
/// <para>
/// A module initializer rather than a fixture, because a fixture protects the tests that remember
/// to use it. What needs protecting is the test that reaches for a default host without thinking
/// about manifests at all — which is every test that has no opinion about them.
/// </para>
/// </remarks>
internal static class ManifestIsolation
{
    /// <summary>Where this assembly looks for a manifest: nowhere.</summary>
    /// <remarks>
    /// Never created. Nothing in these assemblies writes to the resolved default, and if
    /// something ever does, failing on the absent directory is the loud version of quietly
    /// editing the developer's own configuration. Tests that want a manifest pass an explicit
    /// path, which outranks this.
    /// </remarks>
    internal static readonly string NoManifest = Path.Combine(
        Path.GetTempPath(), $"parley-tests-{Guid.NewGuid():N}", "projects.json");

    /// <summary>Runs before any test in the assembly, and before any host is built.</summary>
    [ModuleInitializer]
    internal static void Isolate() =>
        Environment.SetEnvironmentVariable(ProjectManifest.PathVariable, NoManifest);
}

/// <summary>
/// That the isolation above is in force in whichever assembly compiled it.
/// </summary>
/// <remarks>
/// Linked beside <see cref="ManifestIsolation"/> so every assembly that takes the guard also
/// takes its check. A guard in one file and its test in another is how one of them comes to be
/// removed alone.
/// </remarks>
public sealed class ManifestIsolationTests
{
    [Fact]
    public void This_assembly_never_resolves_the_manifest_of_the_machine_it_runs_on()
    {
        // Asserted on what gets resolved rather than on what the variable holds. The variable
        // being set proves nothing about what was resolved from it -- an empty value is set and
        // resolves to the default, which is the entire bug this guard exists for.
        var resolved = ProjectManifest.Locate(null, Environment.GetEnvironmentVariable);

        Assert.NotEqual(ProjectManifest.DefaultPath, resolved);

        // And the resolved file really is absent, so "no projects" is the truth rather than a
        // read that failed. Non-vacuous on a machine with no manifest, where the assertion below
        // it would pass for the wrong reason -- the state every CI runner is in, and the reason
        // this went unnoticed until a developer named a project.
        Assert.True(ProjectManifest.TryRead(resolved, out var projects, out var problem));
        Assert.Null(problem);
        Assert.Empty(projects);
    }
}
