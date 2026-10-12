using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 16: the files the scan reads, rather than the ones it writes.
/// </summary>
/// <remarks>
/// Git stores a symlink's target verbatim, absolute paths included, so a checkout can point its
/// <c>.mcp.json</c> anywhere the person can read. The scan must not follow it out of the checkout.
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

    /// <summary>A file outside every checkout, of the kind a person would mind sharing.</summary>
    private string Elsewhere => Path.Combine(_root, "private.key");

    private string Repository(string name)
    {
        var path = Path.Combine(Checkouts, name);

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    /// <summary>Everything the find says about every repository under the checkouts.</summary>
    /// <remarks>
    /// The whole of each row rather than one field, because a secret that moved into the
    /// suggested line would still be a secret that left the checkout.
    /// </remarks>
    private string Scanned() =>
        string.Join(
            "\n",
            EnrollCommand.Find(Checkouts).Select(entry => string.Join(
                "\n",
                (string[])[entry.Workspace, entry.Agent, entry.Owns ?? string.Empty])));

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

        var manifest = Scanned();

        Assert.DoesNotContain(Secret, manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("somebody-elses-agent", manifest, StringComparison.Ordinal);
    }

}
