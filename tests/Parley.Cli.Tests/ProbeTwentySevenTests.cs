using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 16: the files the scan reads, rather than the ones it writes.
/// </summary>
/// <remarks>
/// <para>
/// 2.3, 2.4 and 2.11 all closed the same hole in three places: a repository ships a tracked file as a
/// symlink and this program follows it out of the checkout. Every one of them was about a <b>write</b>.
/// A read through the same link is the other direction and nothing has probed it.
/// </para>
/// <para>
/// It matters here because <c>enroll scan</c> exists to read a clone and quote what it found into a
/// file the person then reads, edits, and frequently commits. Git stores a symlink's target verbatim,
/// absolute paths included, so a checkout can point <c>README.md</c> anywhere the person can read —
/// and the scan copies a line of whatever is there into the manifest as evidence.
/// </para>
/// </remarks>
public sealed class ProbeTwentySevenTests : IDisposable
{
    private const string Secret = "PRIVATE-KEY-MATERIAL-THAT-IS-LONGER-THAN-TWENTY-CHARACTERS";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe27-" + Path.GetRandomFileName());

    public ProbeTwentySevenTests() => Directory.CreateDirectory(Checkouts);

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

    private string Checkouts => Path.Combine(_root, "checkouts");

    private string ManifestPath => Path.Combine(_root, "enrol.json");

    /// <summary>A file outside every checkout, of the kind a person would mind sharing.</summary>
    private string Elsewhere => Path.Combine(_root, "private.key");

    private string Repository(string name)
    {
        var path = Path.Combine(Checkouts, name);

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    private async Task<string> ScanAsync()
    {
        var code = await EnrollCommand.RunAsync(
            ["scan", Checkouts, ManifestPath],
            selectAll: false,
            new Resolved(Path.Combine(_root, "node.json"), ValueSource.Argument),
            new StringWriter(),
            new StringWriter(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, code);

        return await File.ReadAllTextAsync(ManifestPath, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_readme_that_is_a_link_out_of_the_checkout_is_not_quoted_into_the_manifest()
    {
        // The repository ships `README.md` as a link to something it does not own. Following it puts
        // a line of somebody else's file into a manifest the person reads and often commits — and
        // the scan is pointed at directories of clones precisely because they are not the person's.
        await File.WriteAllTextAsync(
            Elsewhere, Secret + "\n", TestContext.Current.CancellationToken);

        var repository = Repository("orders");

        try
        {
            File.CreateSymbolicLink(Path.Combine(repository, "README.md"), Elsewhere);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
        {
            return; // Without symlink privilege this probe can say nothing.
        }

        Assert.DoesNotContain(Secret, await ScanAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ordinary_readme_is_still_quoted()
    {
        // The control. Refusing to read every README would satisfy the probe above and remove the
        // one thing the evidence is for: showing somebody what their repository already says about
        // itself, so that approving a suggested line is checkable against something.
        var repository = Repository("orders");

        await File.WriteAllTextAsync(
            Path.Combine(repository, "README.md"),
            "# Orders\n\nCarts, totals and the checkout flow for the shop.\n",
            TestContext.Current.CancellationToken);

        Assert.Contains(
            "Carts, totals and the checkout flow",
            await ScanAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_launch_configuration_that_is_a_link_out_of_the_checkout_is_not_read_either()
    {
        // The same question of the other file the scan reads. What comes back from here becomes the
        // suggested agent name and owns line — an identity — so reading it out of a file the
        // repository merely points at is the same defect wearing different clothes.
        await File.WriteAllTextAsync(
            Elsewhere,
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["parley"] = new JsonObject
                    {
                        ["command"] = EnrollmentPlan.AdapterCommand,
                        ["env"] = new JsonObject
                        {
                            ["PARLEY_AGENT"] = "somebody-elses-agent",
                            ["PARLEY_OWNS"] = Secret,
                        },
                    },
                },
            }.ToJsonString(),
            TestContext.Current.CancellationToken);

        var repository = Repository("orders");

        try
        {
            File.CreateSymbolicLink(Path.Combine(repository, ".mcp.json"), Elsewhere);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
        {
            return;
        }

        var manifest = await ScanAsync();

        Assert.DoesNotContain(Secret, manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("somebody-elses-agent", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_readme_that_is_one_enormous_line_does_not_end_the_scan()
    {
        // `File.ReadLines` hands back a line at a time, and a line is however long the file says.
        // The scan's value is that it looks at eleven repositories at once, so one able to exhaust
        // the run is one able to hide the other ten — run 7's rule, applied to the input run 7 did
        // not probe.
        var repository = Repository("huge");

        await File.WriteAllTextAsync(
            Path.Combine(repository, "README.md"),
            new string('x', 24_000_000),
            TestContext.Current.CancellationToken);

        Repository("ordinary");

        Assert.Contains("ordinary", await ScanAsync(), StringComparison.Ordinal);
    }
}
