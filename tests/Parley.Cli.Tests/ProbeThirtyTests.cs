using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Every remaining claim about what was written.
/// </summary>
/// <remarks>
/// <para>
/// A surface's report can disagree with the disk, and a fix is a claim about every caller,
/// so this enumerates the eight places any surface asserts what was or was not written and asks
/// whether each is reachable and true.
/// </para>
/// <para>
/// Six are sound by construction. The two worth a probe are the withdrawal
/// — which claims outright that the files could not be withdrawn — and the acquisition order that
/// decides whether that claim can ever be false.
/// </para>
/// </remarks>
[Integration]
public sealed class ProbeThirtyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe30-" + Path.GetRandomFileName());

    public ProbeThirtyTests()
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

    private string CataloguePath => Path.Combine(_root, "node.json");

    private Task<Enrolment.Outcome> EnrolAsync() =>
        Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, "orders", "Carts and totals.", Unattended: true)
            {
                Enroll = true,
            },
            "alpha",
            CataloguePath,
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_withdrawal_that_cannot_reach_the_catalogue_has_not_touched_the_launch_entry()
    {
        // **The claim under test is `The files could not be withdrawn`**, which asserts that none of
        // them were. It holds because of the acquisition order rather than by luck: the catalogue is
        // taken before the launch configuration is read, so the one failure a person can actually
        // produce — a catalogue somebody else is holding — happens before anything is written.
        await EnrolAsync();

        var before = await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken);

        using (new FileStream(CataloguePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = await Record.ExceptionAsync(() => Enrolment.WithdrawAsync(
                Workspace, "orders", "alpha", CataloguePath, TestContext.Current.CancellationToken));

            Assert.NotNull(failure);

            // Nothing carried, because nothing was written: an InterruptedEnrolment here would mean
            // the sentence the surface prints is false.
            Assert.IsNotType<InterruptedEnrolment>(failure);
        }

        Assert.Equal(
            before,
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_enrolment_that_cannot_reach_the_catalogue_has_not_touched_anything_either()
    {
        // The mirror, and the reason the order is written down in both halves. A held catalogue must
        // stop the run before the launch configuration is replaced, or `Nothing was written.` is
        // false on the commonest collision there is.
        await File.WriteAllTextAsync(
            CataloguePath, "{ \"agents\": {} }", TestContext.Current.CancellationToken);

        using (new FileStream(CataloguePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = await Record.ExceptionAsync(EnrolAsync);

            Assert.NotNull(failure);
            Assert.IsNotType<InterruptedEnrolment>(failure);
        }

        Assert.False(File.Exists(LaunchFile), "the launch configuration was written anyway");
    }

    [Fact]
    public async Task A_catalogue_that_is_not_a_file_at_all_is_the_one_case_that_gets_past_the_hold()
    {
        // **The control, and the case 2.17 was found through.** `File.Exists` is false for a
        // directory, so the hold declines to take one and the failure lands at the write instead —
        // which is precisely why that path can leave the launch configuration replaced. Asserted
        // here so that a future change making the hold refuse a directory outright does not silently
        // remove the only case the surfaces' new branch is written for.
        Directory.CreateDirectory(CataloguePath);

        var failure = await Record.ExceptionAsync(EnrolAsync);

        var interrupted = Assert.IsType<InterruptedEnrolment>(failure);

        Assert.Contains(LaunchFile, interrupted.Written);
        Assert.True(File.Exists(LaunchFile));
    }

    [Fact]
    public async Task A_withdrawal_finds_the_same_directory_absent_rather_than_failing_over_it()
    {
        // The asymmetry that makes the withdrawal's claim safe, stated rather than assumed. The same
        // directory that gets past the enrolment's hold reads as an absent catalogue here, so the
        // withdrawal reports it and finishes instead of stopping half way.
        await EnrolAsync();

        File.Delete(CataloguePath);
        Directory.CreateDirectory(CataloguePath);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", "alpha", CataloguePath, TestContext.Current.CancellationToken);

        Assert.Contains(
            outcome.Notes,
            note => note.Contains("already absent", StringComparison.Ordinal));

        var servers = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!["mcpServers"]!;

        Assert.Empty(servers);
    }
}
