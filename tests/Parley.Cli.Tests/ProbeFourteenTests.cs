using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// The other files a link could redirect.
/// </summary>
/// <remarks>
/// <para>
/// A linked <c>.mcp.json</c> is refused. An enrolment writes <b>three</b> files — the launch
/// configuration, the standing instructions, and the project's catalogue — and a fix in a
/// funnel only two of them use would miss the third: a fix is a claim about every
/// caller, and it is only ever true of the callers a probe happened to run.
/// </para>
/// <para>
/// The standing instructions are the interesting one. They are a tracked file in the same checkout,
/// appended to rather than replaced, and appending through a link is the same primitive with a
/// different verb.
/// </para>
/// </remarks>
[Integration]
public sealed class ProbeFourteenTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe14-" + Path.GetRandomFileName());

    public ProbeFourteenTests() => Directory.CreateDirectory(_root);

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

    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    private static EnrollmentEntry Entry(string workspace) =>
        new(workspace, "orders", "Carts and totals.", Unattended: true)
        {
            Enroll = true,
        };

    /// <summary>Links <paramref name="at"/> to <paramref name="target"/>, or says it cannot.</summary>
    private static bool Link(string at, string target)
    {
        try
        {
            File.CreateSymbolicLink(at, target);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
        {
            return false; // No symlink privilege here; the property is unobservable on this machine.
        }
    }

    private async Task<bool> EnrolAsync(string workspace, string catalogue)
    {
        try
        {
            await Enrolment.AddAsync(
                Entry(workspace),
                "alpha",
                catalogue,
                Path.Combine(workspace, "CLAUDE.md"),
                TestContext.Current.CancellationToken);

            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Standing_instructions_that_are_a_link_do_not_carry_the_append_out_of_the_repository()
    {
        // **The file 2.3's fix did not cover.** The peers frame is appended to `CLAUDE.md`, which is
        // as tracked as `.mcp.json` and as easy for a repository to ship as a link. Appending through
        // one writes the frame into whatever it points at — and unlike the launch configuration, this
        // path replaces the file wholesale rather than merging into it.
        var workspace = Workspace("orders");
        var outside = Path.Combine(_root, "outside.md");

        await File.WriteAllTextAsync(outside, "not ours", TestContext.Current.CancellationToken);

        if (!Link(Path.Combine(workspace, "CLAUDE.md"), outside))
        {
            return;
        }

        await EnrolAsync(workspace, Path.Combine(_root, "node.json"));

        Assert.Equal(
            "not ours",
            await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_catalogue_that_is_a_link_does_not_carry_the_write_out_either()
    {
        // The third file. This one goes through the same funnel the launch configuration does, so the
        // expectation is that the refusal already covers it — asserted rather than assumed, because
        // "the fix covers this too" is a sentence that is often wrong.
        var workspace = Workspace("orders");
        var outside = Path.Combine(_root, "outside.json");

        await File.WriteAllTextAsync(outside, "not ours", TestContext.Current.CancellationToken);

        var catalogue = Path.Combine(_root, "node.json");

        if (!Link(catalogue, outside))
        {
            return;
        }

        await EnrolAsync(workspace, catalogue);

        Assert.Equal(
            "not ours",
            await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_withdrawal_does_not_write_through_a_link_either()
    {
        // Withdrawal edits the same two files and is the path that removes rather than adds. A link
        // followed here would truncate whatever it points at down to an empty server list.
        var workspace = Workspace("orders");
        var outside = Path.Combine(_root, "outside.json");

        await File.WriteAllTextAsync(outside, "not ours", TestContext.Current.CancellationToken);

        if (!Link(Path.Combine(workspace, ".mcp.json"), outside))
        {
            return;
        }

        var outcome = await Enrolment.WithdrawAsync(
            workspace, "orders", "alpha", null, TestContext.Current.CancellationToken);

        Assert.Equal(
            "not ours",
            await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Path.Combine(workspace, ".mcp.json"), outcome.Written);
    }

    [Fact]
    public async Task A_link_is_named_in_the_refusal_so_somebody_can_act_on_it()
    {
        // A refusal nobody can act on is a wall. The person needs to know which file, and that it is
        // a link rather than a permission problem — those two lead somewhere completely different.
        var workspace = Workspace("orders");
        var outside = Path.Combine(_root, "outside.json");

        await File.WriteAllTextAsync(outside, "not ours", TestContext.Current.CancellationToken);

        if (!Link(Path.Combine(workspace, ".mcp.json"), outside))
        {
            return;
        }

        var refusal = await Assert.ThrowsAsync<InvalidDataException>(() => Enrolment.AddAsync(
            Entry(workspace),
            "alpha",
            Path.Combine(_root, "node.json"),
            Path.Combine(workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken));

        Assert.Contains(".mcp.json", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("link", refusal.Message, StringComparison.Ordinal);
    }
}
