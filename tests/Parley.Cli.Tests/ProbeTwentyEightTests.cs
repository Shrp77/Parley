using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 17: a bulk run that stops half way.
/// </summary>
/// <remarks>
/// <para>
/// 2.14 asked what one repository's failed enrolment leaves behind. `parley enroll apply` writes a
/// list of them, one after another, and a refusal on the fifth arrives with four already on disk. The
/// same question one level up, and the level the command's own comment says it cares about: it refuses
/// a half-enrolled set on principle, because "the repository that missed out is indistinguishable from
/// one you never meant to enroll".
/// </para>
/// <para>
/// Validation is hoisted ahead of every write for exactly that reason. What cannot be hoisted is a
/// target file this could not merge into, because knowing that means reading each repository's own
/// launch configuration — so this is the one refusal that necessarily arrives mid-list.
/// </para>
/// </remarks>
public sealed class ProbeTwentyEightTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe28-" + Path.GetRandomFileName());

    public ProbeTwentyEightTests() => Directory.CreateDirectory(Checkouts);

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

    private string Workspace(string name) => Path.Combine(Checkouts, name);

    private string Repository(string name, string? launch = null)
    {
        var path = Workspace(name);

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        if (launch is not null)
        {
            File.WriteAllText(Path.Combine(path, ".mcp.json"), launch);
        }

        return path;
    }

    /// <summary>A manifest enrolling three repositories, in a known order.</summary>
    private async Task WriteManifestAsync(params string[] names)
    {
        var rows = new JsonArray();

        foreach (var name in names)
        {
            rows.Add(new JsonObject
            {
                ["workspace"] = Workspace(name).Replace('\\', '/'),
                ["agent"] = name,
                ["owns"] = $"What {name} owns. Not what it does not.",
                ["unattended"] = false,
                ["enroll"] = true,
                ["owns_approved"] = true,
            });
        }

        await File.WriteAllTextAsync(
            ManifestPath,
            new JsonObject { ["agents"] = rows }.ToJsonString(),
            TestContext.Current.CancellationToken);
    }

    private async Task<(int Code, string Output, string Error)> ApplyAsync()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await EnrollCommand.RunAsync(
            ["apply", ManifestPath],
            selectAll: null,
            new Resolved(Path.Combine(_root, "node.json"), ValueSource.Argument),
            output,
            error,
            TestContext.Current.CancellationToken);

        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task A_run_that_stops_on_the_second_repository_says_the_first_was_written()
    {
        // The person's reasonable reading of "this manifest could not be applied" is that their
        // repositories are as they were. One of them is now announcing itself as an agent, and
        // nothing in the run says which.
        Repository("orders");
        Repository("billing", "{ \"mcpServers\": [] }");
        Repository("inventory");

        await WriteManifestAsync("orders", "billing", "inventory");

        var (code, output, error) = await ApplyAsync();

        Assert.NotEqual(0, code);

        // It really did get that far: the first repository is enrolled on disk.
        Assert.True(
            File.Exists(Path.Combine(Workspace("orders"), ".mcp.json")),
            "the first repository was never written, so this probe proves nothing");

        Assert.Contains("orders", output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_stops_names_the_repositories_it_never_reached()
    {
        // The other half of the same sentence, and the one the command's own principle rests on. A
        // repository that missed out is indistinguishable from one nobody meant to enrol, so a run
        // that stops has to say where it stopped rather than only why.
        Repository("orders");
        Repository("billing", "{ \"mcpServers\": [] }");
        Repository("inventory");

        await WriteManifestAsync("orders", "billing", "inventory");

        var (_, output, error) = await ApplyAsync();

        Assert.False(
            File.Exists(Path.Combine(Workspace("inventory"), ".mcp.json")),
            "the third repository was written, so there is nothing unreached to name");

        Assert.Contains("inventory", output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_with_nothing_wrong_enrols_every_repository()
    {
        // The control. Both assertions above are about a run that stops, and a command that stopped
        // on every run would satisfy them.
        Repository("orders");
        Repository("billing");
        Repository("inventory");

        await WriteManifestAsync("orders", "billing", "inventory");

        var (code, _, error) = await ApplyAsync();

        Assert.Equal(0, code);
        Assert.Empty(error);

        foreach (var name in (string[])["orders", "billing", "inventory"])
        {
            Assert.True(File.Exists(Path.Combine(Workspace(name), ".mcp.json")), name);
        }
    }
}
