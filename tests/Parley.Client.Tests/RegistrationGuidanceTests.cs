using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That the registration instructions name this project's broker, and never a credential's value.
/// </summary>
/// <remarks>
/// <para>
/// The page is instructions rather than a form on purpose. An agent's identity comes from the
/// launch configuration of the process that will act as it, so a console that registered one
/// would be naming an agent on behalf of a process that had not started — the forgery the trust
/// model rests on preventing. These check that the page still says so.
/// </para>
/// <para>
/// The endpoint is the other half. Instructions carrying a placeholder, or carrying the first
/// project's endpoint on every project's page, send somebody's agent to the wrong broker — and it
/// registers successfully there, so nothing fails and the mistake surfaces later as an agent
/// missing from a roster it was never in.
/// </para>
/// </remarks>
public sealed class RegistrationGuidanceTests
{
    private static string Page =>
        File.ReadAllText(RepositoryPath("src/Parley.Web/Pages/Projects/Register.cshtml"));

    [Fact]
    public void The_endpoint_comes_from_the_selected_project()
    {
        // Rather than from a constant. A placeholder in the instructions is a working
        // registration against the wrong broker, which fails at no point a person is watching.
        Assert.Contains("@Model.Endpoint", Page, StringComparison.Ordinal);

        var literal = Regex.Matches(
            Page, @"https?://(?!127\.0\.0\.1|localhost)[^\s""<]+", RegexOptions.None,
            TimeSpan.FromSeconds(2));

        Assert.True(
            literal.Count == 0,
            "the instructions carry a hard-coded endpoint, which sends an agent to a broker that "
            + "is not this project's: " + string.Join(", ", literal.Select(m => m.Value)));
    }

    [Fact]
    public void It_says_the_identity_cannot_be_supplied_by_a_call()
    {
        Assert.Contains("cannot forge", Page, StringComparison.Ordinal);
        Assert.Contains("No call can supply it", Page, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_how_to_confirm_the_registration_took()
    {
        // Instructions that end at "write this file" leave somebody unable to tell success from a
        // server that never started. The confirmation is the roster, and the page links it.
        Assert.Contains("/Projects/Agents?project=", Page, StringComparison.Ordinal);
        Assert.Contains("appears there once its session has attached", Page, StringComparison.Ordinal);
    }

    [Fact]
    public void No_page_in_the_console_renders_a_resolved_credential()
    {
        // Credentials are references and never values -- env:, keychain:, file: -- and the broker
        // resolves them at invocation without persisting or logging them. A console that printed
        // a resolved one would undo that in the one place it is most likely to be screenshotted.
        var pages = Directory.GetFiles(
            RepositoryPath("src/Parley.Web/Pages"), "*.cshtml", SearchOption.AllDirectories);

        Assert.NotEmpty(pages);

        var leaking = pages
            .Where(p => Regex.IsMatch(
                File.ReadAllText(p),
                @"Model\.(Credential|Secret|Token|Password|ApiKey|Resolved)",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            leaking.Count == 0,
            "a page renders something that reads as a resolved credential: "
            + string.Join(", ", leaking));
    }

    [Fact]
    public void The_console_never_reads_a_credential_reference_at_all()
    {
        // Stronger than not rendering one, and the reason the check above is not enough on its
        // own: resolution is the broker's, at invocation. A console that resolved a reference
        // would be a second holder of the value, and the guarantee that makes the manifest safe
        // to commit is that there is exactly one.
        var sources = Directory.GetFiles(
            RepositoryPath("src/Parley.Web"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(sources);

        var resolving = sources
            .Where(p => Regex.IsMatch(
                File.ReadAllText(p),
                @"""(env|keychain|file):|ResolveCredential",
                RegexOptions.None, TimeSpan.FromSeconds(2)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            resolving.Count == 0,
            "the console resolves a credential reference, which makes it a second holder of a "
            + "value only the broker should ever hold: " + string.Join(", ", resolving));
    }

    [Fact]
    public void It_warns_that_a_tracked_config_follows_the_branch()
    {
        // The failure this prevents is silent and recurring. `.mcp.json` is usually tracked, so
        // an entry written on a feature branch is on disk while that branch is out and gone on
        // one that predates it — the session starts, the tools are absent, nothing errors, and
        // the peer that sent a request waits, its work flagged as never collected.
        //
        // Cost real hours on a real deployment before it was written down anywhere.
        Assert.Contains("which branch is checked out", Page, StringComparison.Ordinal);
        Assert.Contains("branch everything else is cut from", Page, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_place_that_tells_somebody_to_write_the_file_says_it()
    {
        // Three surfaces tell a person to write `.mcp.json`, and a warning on one of them is a
        // warning two thirds of readers never see.
        foreach (var guidance in new[]
        {
            "docs/claude-code.md",
            "plugins/parley/commands/setup.md",
            "src/Parley.Web/Pages/Projects/Register.cshtml"
        })
        {
            Assert.Contains(
                "which branch is checked out",
                File.ReadAllText(RepositoryPath(guidance)),
                StringComparison.Ordinal);
        }
    }

    /// <summary>Finds a path in the repository from wherever the tests run.</summary>
    /// <param name="relative">The path, relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 }
               && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
