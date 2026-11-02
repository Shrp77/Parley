using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// A bulk run that stops half way.
/// </summary>
/// <remarks>
/// <para>
/// `parley enroll` writes a list of repositories, one after another, and a refusal on the fifth
/// arrives with four already on disk. That is the question of what a failed enrolment leaves behind,
/// one level up, and the level the command's own comment says it cares about: it refuses
/// a half-enrolled set on principle, because "the repository that missed out is indistinguishable from
/// one you never meant to enroll".
/// </para>
/// <para>
/// Validation is hoisted ahead of every write for exactly that reason. What cannot be hoisted is a
/// target file this could not merge into, because knowing that means reading each repository's own
/// launch configuration — so this is the one refusal that necessarily arrives mid-list.
/// </para>
/// </remarks>
[Integration]
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

    /// <summary>Three repositories chosen for enrolment, in a known order.</summary>
    /// <remarks>
    /// The order is the whole of this probe: these were the rows of a manifest and they are the rows
    /// a surface hands back, and either way the writing walks them one after another. Which carrier
    /// they arrived in decides nothing about where a run stops.
    /// </remarks>
    private EnrollmentEntry[] Chosen(params string[] names) =>
        [.. names.Select(name => new EnrollmentEntry(
            Workspace(name).Replace('\\', '/'),
            name,
            $"What {name} owns. Not what it does not.",
            Unattended: false)
        {
            Enroll = true,
        })];

    private async Task<(int Code, string Output, string Error)> ApplyAsync(params string[] names)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await EnrollCommand.EnrolAsync(
            Chosen(names),
            describe: false,
            new Resolved(Path.Combine(_root, "node.json"), ValueSource.Argument),
            output,
            error,
            cancellationToken: TestContext.Current.CancellationToken);

        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task A_run_that_stops_on_the_second_repository_says_the_first_was_written()
    {
        // The person's reasonable reading of "these repositories could not be enrolled" is that their
        // repositories are as they were. One of them is now announcing itself as an agent, and
        // nothing in the run says which.
        Repository("orders");
        Repository("billing", "{ \"mcpServers\": [] }");
        Repository("inventory");

        var (code, output, error) = await ApplyAsync("orders", "billing", "inventory");

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

        var (_, output, error) = await ApplyAsync("orders", "billing", "inventory");

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

        var (code, _, error) = await ApplyAsync("orders", "billing", "inventory");

        Assert.Equal(0, code);
        Assert.Empty(error);

        foreach (var name in (string[])["orders", "billing", "inventory"])
        {
            Assert.True(File.Exists(Path.Combine(Workspace(name), ".mcp.json")), name);
        }
    }
}
