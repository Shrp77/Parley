using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What a scan says a repository already is.
/// </summary>
/// <remarks>
/// <para>
/// <b>A repository has as many enrolments as it has entries, and "the first one" is never the right
/// answer.</b> The reader took <c>mcpServers.parley.env</c> by literal key, so once an entry could sit
/// under another name it would have reported every multiply-enrolled repository as enrolled
/// nowhere — and the person most likely to act on that is the one about to enrol it into a project it
/// is already in.
/// </para>
/// <para>
/// <b>Three answers, not two.</b> Enrolled nowhere and could-not-be-read are distinguished, because
/// the first is a repository to enrol and the second is a file to look at. Collapsing them is the
/// same failure the install scripts avoid when a broker cannot be asked.
/// </para>
/// </remarks>
public sealed class ScanReportsEveryProjectTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-scan-" + Path.GetRandomFileName());

    public ScanReportsEveryProjectTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private string Repository(string name, string? launch)
    {
        var workspace = Path.Combine(_root, name);

        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));

        if (launch is not null)
        {
            File.WriteAllText(Path.Combine(workspace, ".mcp.json"), launch);
        }

        return workspace;
    }

    /// <summary>Everything the find says about every repository under the root.</summary>
    /// <remarks>
    /// The whole of each row rather than one field, because these assertions are about what a person
    /// is shown and a row that quietly moved a fact from the evidence to the name would still be
    /// telling them. It was the serialised manifest before the file went; the rows are what the file
    /// held.
    /// </remarks>
    private string Scanned() =>
        string.Join(
            "\n",
            EnrollCommand.Find(_root).Select(entry => string.Join(
                "\n",
                (string[])[entry.Workspace, entry.Agent, entry.Owns ?? string.Empty, .. entry.Evidence ?? []])));

    [Fact]
    public void A_repository_in_three_projects_has_all_three_reported()
    {
        Repository("inventory", """
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": {
                  "PARLEY_AGENT": "inventory", "PARLEY_PROJECT": "alpha" } },
                "parley-beta": { "command": "parley-mcp", "env": {
                  "PARLEY_AGENT": "stock", "PARLEY_PROJECT": "beta" } },
                "parley-gamma": { "command": "parley-mcp", "env": {
                  "PARLEY_AGENT": "warehouse", "PARLEY_PROJECT": "gamma" } }
              }
            }
            """);

        var written = Scanned();

        Assert.Contains("alpha", written, StringComparison.Ordinal);
        Assert.Contains("beta", written, StringComparison.Ordinal);
        Assert.Contains("gamma", written, StringComparison.Ordinal);

        // Each project's own name for it, not one name three times.
        Assert.Contains("stock", written, StringComparison.Ordinal);
        Assert.Contains("warehouse", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_legacy_entry_is_reported_as_the_project_it_answers_in()
    {
        Repository("orders", """
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": {
                  "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" } }
              }
            }
            """);

        Assert.Contains("answers in: alpha", Scanned(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_enrolled_nowhere_says_nothing_about_projects()
    {
        Repository("fresh", null);

        Assert.DoesNotContain("answers in", Scanned(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_launch_configuration_that_cannot_be_read_is_not_the_same_as_no_enrolment()
    {
        Repository("broken", "{ this is not json");

        var written = Scanned();

        Assert.Contains("could not be read", written, StringComparison.Ordinal);
        Assert.DoesNotContain("answers in", written, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_naming_no_project_is_reported_as_naming_none()
    {
        // Every repository enrolled before projects existed. Reporting it as belonging nowhere would
        // be wrong — it belongs to whatever broker it reaches — and reporting a project it does not
        // name would be an invention.
        Repository("legacy", """
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } }
              }
            }
            """);

        Assert.Contains("no project", Scanned(), StringComparison.Ordinal);
    }
}
