using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 1, run 9: the manifest's writers, now that the gate shares their lock.
/// </summary>
/// <remarks>
/// <para>
/// Run 8 moved the grant gate onto the lock <c>ProjectManifest</c> already keeps for its own writes.
/// That is one mechanism rather than two — and it puts <c>parley project add</c> and a bulk enrolment
/// on the same lock for the first time. Two things follow that nothing has checked: what a manifest
/// write does when a grant is in progress, and whether the terminal's own sequence, which writes the
/// manifest and *then* takes the gate, can end up waiting for itself.
/// </para>
/// <para>
/// A lock that is not re-entrant is the right kind, and it is also the kind that hangs when one call
/// path takes it twice. The bound turns that into a two-second stall and a failure rather than a
/// wedged tool, which is why the bound exists — but a probe should say which of the two this is.
/// </para>
/// </remarks>
public sealed class ProbeNineTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe9-" + Path.GetRandomFileName());

    public ProbeNineTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

        ProjectManifest.Write(ManifestPath, [Project(catalogue: null)]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string ManifestPath => Path.Combine(_root, "projects.json");

    private string Workspace => Path.Combine(_root, "orders").Replace(Path.DirectorySeparatorChar, '/');

    private string CataloguePath => Path.Combine(_root, "alpha-node.json");

    private ProjectSettings Project(string? catalogue) =>
        new(ProjectName.Create("alpha"), Path.Combine(_root, "alpha.db"),
            "http://127.0.0.1:51707", catalogue, null);

    private static EnrollmentEntry Entry(string workspace) =>
        new(workspace, "orders", "Carts and totals.", Unattended: true)
        {
            Enroll = true,
            OwnsApproved = true,
        };

    [Fact]
    public async Task Recording_a_catalogue_and_then_enrolling_does_not_wait_for_itself()
    {
        // The terminal's own sequence: a project that names no catalogue gets one written into the
        // manifest, and the enrolment follows. Both take the same lock now. They are sequential and
        // must stay so — a version that took the gate first and wrote the manifest inside it would
        // wait two seconds and fail, on the ordinary path of adding the first agent to a project.
        ProjectManifest.Write(ManifestPath, [Project(CataloguePath)]);

        var enrolling = Enrolment.AddAsync(
            Entry(Workspace),
            "alpha",
            CataloguePath,
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken,
            [Project(CataloguePath)],
            ManifestPath);

        var finished = await Task.WhenAny(enrolling, Task.Delay(10_000, TestContext.Current.CancellationToken));

        Assert.Same(enrolling, finished);

        var outcome = await enrolling;

        Assert.Contains(Path.Combine(Workspace, ".mcp.json"), outcome.Written);
    }

    [Fact]
    public async Task A_manifest_write_during_a_grant_leaves_the_manifest_readable()
    {
        // The interaction run 8 created. Whether the write waits, or is refused, is a judgement — but
        // it must not leave the manifest unreadable, and it must not leave a half-written sibling
        // that a later read mistakes for the real thing.
        using var gate = await Catalogues.GateAsync(ManifestPath, TestContext.Current.CancellationToken);

        Assert.NotNull(gate);

        // Through `TryUpdate`, which is what `parley project add`, `set` and `remove` all call, and
        // the only writer that takes the lock. `Write` is the raw primitive underneath it and takes
        // nothing — the probe used that first and reported a finding that was its own mistake.
        ProjectManifest.TryUpdate(
            ManifestPath, _ => [Project(CataloguePath)], out _);

        Assert.True(
            ProjectManifest.TryRead(ManifestPath, out var projects, out var problem),
            $"the manifest is no longer readable after a write during a grant: {problem}");

        Assert.Single(projects);
    }

    [Fact]
    public async Task A_grant_and_a_manifest_write_do_not_both_proceed()
    {
        // What sharing one lock is *for*. If both went ahead, a grant would be deciding against a
        // deployment that changed underneath it — the thing the gate exists to prevent, arriving by
        // the other door.
        using var gate = await Catalogues.GateAsync(ManifestPath, TestContext.Current.CancellationToken);

        var wrote = ProjectManifest.TryUpdate(
            ManifestPath, _ => [Project(CataloguePath)], out var problem);

        Assert.False(wrote, "a manifest change went ahead while a grant held the lock");
        Assert.NotNull(problem);
    }

    [Fact]
    public async Task The_manifest_can_be_written_again_once_the_grant_is_over()
    {
        // The other half: a lock that is never released is worse than one never taken.
        var gate = await Catalogues.GateAsync(ManifestPath, TestContext.Current.CancellationToken);

        gate?.Dispose();

        Assert.True(
            ProjectManifest.TryUpdate(ManifestPath, _ => [Project(CataloguePath)], out var problem),
            problem);

        Assert.True(ProjectManifest.TryRead(ManifestPath, out var projects, out _));
        Assert.Equal(CataloguePath, Assert.Single(projects).NodeConfig);
    }
}
