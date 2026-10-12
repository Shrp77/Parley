using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 10: the third file, and the one a person wrote.
/// </summary>
/// <remarks>
/// <para>
/// This feature writes three files. Two are the tooling's own — the launch configuration and the node
/// catalogue — and lens 1 gave both an exclusive handle spanning their read and their write, because
/// a repository in several projects has several writers. <c>CLAUDE.md</c> is the third, is entirely
/// somebody's prose, and is read at the top of the call and written at the bottom.
/// </para>
/// <para>
/// The window between is not nothing: the launch configuration is acquired in it, and that acquisition
/// retries for two seconds against another writer. What lands in the window is a person saving in an
/// editor, and what this asks is whether their work is still there afterwards.
/// </para>
/// </remarks>
public sealed class ProbeTwentyOneTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe21-" + Path.GetRandomFileName());

    public ProbeTwentyOneTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
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

    private string Workspace => Path.Combine(_root, "orders");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private string Instructions => Path.Combine(Workspace, "CLAUDE.md");

    private Task<Enrolment.Outcome> EnrolAsync() =>
        Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, "orders", "Carts and totals.", Unattended: false)
            {
                Enroll = true,
            },
            "alpha",
            Path.Combine(_root, "node.json"),
            Instructions,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Runs an enrolment with the launch configuration held for a moment, and does
    /// <paramref name="meanwhile"/> inside that window.
    /// </summary>
    /// <remarks>
    /// The window is the tool's own: <c>HoldAsync</c> retries against an exclusive handle, so holding
    /// the launch configuration briefly widens the gap between the instructions being read and being
    /// written without any seam being added to the subject.
    /// </remarks>
    private async Task<Enrolment.Outcome> DuringAsync(Func<Task> meanwhile)
    {
        await File.WriteAllTextAsync(
            LaunchFile, "{ \"mcpServers\": {} }", TestContext.Current.CancellationToken);

        var held = new FileStream(LaunchFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var enrolling = Task.Run(EnrolAsync, TestContext.Current.CancellationToken);

        // Long enough for the instructions to have been read and the launch acquisition to be
        // retrying, and far short of the two seconds after which it gives up.
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await meanwhile();

        await held.DisposeAsync();

        return await enrolling;
    }

    [Fact]
    public async Task An_edit_made_while_the_enrolment_runs_is_not_overwritten()
    {
        // The ordinary case: somebody has the repository open, saves a paragraph, and the enrolment
        // writes back the copy it read before they typed it.
        await File.WriteAllTextAsync(
            Instructions, "# Orders\n\nWhat this repository owns.\n", TestContext.Current.CancellationToken);

        await DuringAsync(() => File.WriteAllTextAsync(
            Instructions,
            "# Orders\n\nWhat this repository owns.\n\nA paragraph somebody just wrote.\n",
            TestContext.Current.CancellationToken));

        var after = await File.ReadAllTextAsync(Instructions, TestContext.Current.CancellationToken);

        Assert.Contains("A paragraph somebody just wrote.", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_created_while_the_enrolment_runs_is_not_replaced_by_the_frame()
    {
        // The sharper half. With no instructions file the plan is *create*, so the write is not an
        // append at all -- whatever arrived in the window is replaced wholesale by the frame rather
        // than having the frame added to it.
        Assert.False(File.Exists(Instructions));

        await DuringAsync(() => File.WriteAllTextAsync(
            Instructions,
            "# Orders\n\nEverything this repository knows about itself.\n",
            TestContext.Current.CancellationToken));

        var after = await File.ReadAllTextAsync(Instructions, TestContext.Current.CancellationToken);

        Assert.Contains(
            "Everything this repository knows about itself.", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bulk_path_declines_a_linked_instructions_file_too()
    {
        // 2.4 gave `AddAsync` a link check on this file, for the reason 2.3 gave one to the launch
        // configuration: `CLAUDE.md` is as tracked as `.mcp.json` and as easy for a repository to
        // ship as a symlink. `WriteAsync` is the other writer — the one `parley enroll apply` takes,
        // which is pointed at whole directories of clones somebody else wrote — and it was not part
        // of that claim.
        var outside = Path.Combine(_root, "somewhere-else.md");

        await File.WriteAllTextAsync(outside, "Somebody else's file.\n", TestContext.Current.CancellationToken);

        try
        {
            File.CreateSymbolicLink(Instructions, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
        {
            return; // Without symlink privilege this probe can say nothing.
        }

        var entry = new EnrollmentEntry(Workspace, "orders", "Carts and totals.", Unattended: false)
        {
            Enroll = true,
        };

        await Enrolment.WriteAsync(
            (launch, instructions) =>
                EnrollmentPlan.Describe(entry, launch, instructions, false, "alpha", null, null),
            Workspace,
            Instructions,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "Somebody else's file.\n",
            await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Nothing_a_person_wrote_is_lost_when_no_one_is_editing()
    {
        // The control. Without it the two above prove only that concurrency is involved, not that
        // the file is preserved at all.
        await File.WriteAllTextAsync(
            Instructions, "# Orders\n\nWhat this repository owns.\n", TestContext.Current.CancellationToken);

        await EnrolAsync();

        var after = await File.ReadAllTextAsync(Instructions, TestContext.Current.CancellationToken);

        Assert.Contains("What this repository owns.", after, StringComparison.Ordinal);
        Assert.Contains(EnrollmentPlan.PeersHeading, after, StringComparison.Ordinal);
    }
}
