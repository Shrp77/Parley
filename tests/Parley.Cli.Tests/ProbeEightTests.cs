using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What the machine-wide gate does to everybody else.
/// </summary>
/// <remarks>
/// <para>
/// A machine-wide gate is held on the project manifest — for the length of an
/// <c>enroll apply</c>, which over eighteen repositories is not a moment. **Every other thing on the
/// machine reads that file**: the terminal, the web UI, the node, and every operator command reads it
/// at start-up to find out which deployment it is talking to.
/// </para>
/// <para>
/// A gate that excludes readers would take the whole machine down for the length of a bulk enrolment,
/// and the failure would look like the manifest being broken rather than busy. This asks whether it
/// does.
/// </para>
/// </remarks>
[Integration]
public sealed class ProbeEightTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe8-" + Path.GetRandomFileName());

    public ProbeEightTests()
    {
        Directory.CreateDirectory(_root);

        ProjectManifest.Write(
            Root,
            [new ProjectSettings(ProjectName.Create("alpha"), Store, "http://127.0.0.1:51707", null)]);
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

    // **The root rather than the manifest file.** `--config` and PARLEY_CONFIG name the directory
    // this machine keeps its Parley files in; the projects are the folders under it.
    private string Root => _root;

    private string Store => Path.Combine(_root, "alpha.db");

    [Fact]
    public async Task Everything_that_reads_the_manifest_can_still_read_it_while_the_gate_is_held()
    {
        // The probe. `parley registry`, `parley terminal`, the web UI and the node all begin by reading
        // this file; a bulk enrolment holding it against them would stop the machine for as long as
        // the enrolment ran, and the message they would print is about a file that cannot be read
        // rather than one that is busy.
        using var gate = await Catalogues.GateAsync(Root, TestContext.Current.CancellationToken);

        Assert.NotNull(gate);

        var read = ProjectManifest.TryRead(Root, out var projects, out var problem);

        Assert.True(read, $"the manifest could not be read while the gate was held: {problem}");
        Assert.Single(projects);
    }

    [Fact]
    public async Task Two_holders_of_the_gate_still_exclude_each_other()
    {
        // The other half, and the half that must not be traded away to buy the first: the gate exists
        // so that checking whether another project starts a workspace and recording that this one
        // does are one act.
        using var first = await Catalogues.GateAsync(Root, TestContext.Current.CancellationToken);

        Assert.NotNull(first);

        var second = Catalogues.GateAsync(Root, TestContext.Current.CancellationToken);

        var settled = await Task.WhenAny(second, Task.Delay(400, TestContext.Current.CancellationToken));

        Assert.NotSame(second, settled);

        first.Dispose();

        (await second)?.Dispose();
    }

    [Fact]
    public async Task A_reader_holding_a_projects_configuration_does_not_stop_the_gate_being_taken()
    {
        // The inverse, and the deadlock that would follow from getting it wrong: a terminal sitting with
        // a project's configuration open would otherwise make every enrolment on the machine wait two
        // seconds and then fail.
        //
        // **Pointed at what a reader actually holds**: a project's configuration. The gate takes a
        // lock of its own and never the thing anybody reads.
        using var reading = new FileStream(
            ProjectFolder.ConfigurationFor(Root, ProjectName.Create("alpha")),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        using var gate = await Catalogues.GateAsync(Root, TestContext.Current.CancellationToken);

        Assert.NotNull(gate);
    }

    [Fact]
    public async Task The_gate_is_released_even_when_what_it_guards_throws()
    {
        // A refusal inside the guarded region is the ordinary case. A gate
        // left held by an exception would wedge every enrolment on the machine until the process
        // ended.
        try
        {
            using var gate = await Catalogues.GateAsync(Root, TestContext.Current.CancellationToken);

            throw new InvalidDataException("as FR-013 does");
        }
        catch (InvalidDataException)
        {
        }

        using var again = await Catalogues.GateAsync(Root, TestContext.Current.CancellationToken);

        Assert.NotNull(again);
    }
}
