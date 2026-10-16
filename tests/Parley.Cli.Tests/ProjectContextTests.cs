using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Which project a command's context chooses, and what a repository's launch configuration names.
/// </summary>
/// <remarks>
/// <para>
/// A command acts on the project <c>--project</c> names, then the terminal's <c>PARLEY_PROJECT</c>, then
/// the repository it runs in when that names exactly one project this machine describes, then the only project; otherwise
/// it refuses. A saved "current project" was offered and declined, because chosen in one window it would quietly apply to
/// a purge in another.
/// </para>
/// <para>
/// The choice is a pure function and is held to each rung here; the repository is read from real files, because what
/// counts as the repository — the nearest <c>.git</c>, and never a launch configuration above it — is the part a
/// reasoned version would get wrong.
/// </para>
/// </remarks>
public sealed class ProjectContextTests : IDisposable
{
    private static readonly IReadOnlyList<ProjectSettings> Two = [Project("alpha"), Project("beta")];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-context-" + Guid.NewGuid().ToString("N"));

    public ProjectContextTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static ProjectSettings Project(string name) =>
        new(ProjectName.Create(name), $"C:/stores/{name}.db", "http://127.0.0.1:1", null);

    private static (bool Chosen, string? Name, string? Source, string? Problem) Select(
        string? named, string? variable, string[] repository, IReadOnlyList<ProjectSettings> projects)
    {
        var chosen = ProjectResolution.TrySelect(named, variable, repository, projects, out var project, out var source, out var problem);

        return (chosen, project?.Name.Value, source, problem);
    }

    [Fact]
    public void The_flag_wins_over_the_terminal_and_the_repository()
    {
        var (chosen, name, source, _) = Select("beta", "alpha", ["alpha"], Two);

        Assert.True(chosen);
        Assert.Equal("beta", name);
        Assert.Equal("named with --project", source);
    }

    [Fact]
    public void The_terminal_wins_over_the_repository()
    {
        var (chosen, name, source, _) = Select(null, "beta", ["alpha"], Two);

        Assert.True(chosen);
        Assert.Equal("beta", name);
        Assert.Equal("from PARLEY_PROJECT", source);
    }

    [Fact]
    public void A_terminal_naming_a_project_this_machine_does_not_describe_is_refused_and_says_how_to_clear_it()
    {
        // Somebody set it, so passing over it would act somewhere they did not choose.
        var (chosen, name, _, problem) = Select(null, "gamma", ["alpha"], Two);

        Assert.False(chosen);
        Assert.Null(name);
        Assert.Contains("gamma", problem, StringComparison.Ordinal);
        Assert.Contains("parley use --clear", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_repository_chooses_on_a_machine_naming_several()
    {
        var (chosen, name, source, _) = Select(null, null, ["beta"], Two);

        Assert.True(chosen);
        Assert.Equal("beta", name);
        Assert.Equal("from this repository's .mcp.json", source);
    }

    [Fact]
    public void A_repository_naming_two_projects_this_machine_describes_is_refused()
    {
        var (chosen, _, _, problem) = Select(null, null, ["alpha", "beta"], Two);

        Assert.False(chosen);
        Assert.Contains("alpha", problem, StringComparison.Ordinal);
        Assert.Contains("beta", problem, StringComparison.Ordinal);
        Assert.Contains("--project", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_naming_only_projects_that_live_elsewhere_falls_through_to_the_only_project()
    {
        // A machine with one project must keep resolving as it did; the command says which it took.
        var (chosen, name, source, _) = Select(null, null, ["gamma"], [Project("alpha")]);

        Assert.True(chosen);
        Assert.Equal("alpha", name);
        Assert.Equal("the only project on this machine", source);
    }

    [Fact]
    public void Nothing_choosing_between_several_is_refused_and_offers_a_terminal_project()
    {
        var (chosen, _, _, problem) = Select(null, null, [], Two);

        Assert.False(chosen);
        Assert.Contains("none was named", problem, StringComparison.Ordinal);
        Assert.Contains("parley use <name>", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_with_no_projects_needs_none_whatever_the_repository_says()
    {
        var (chosen, name, source, problem) = Select(null, null, ["gamma"], []);

        Assert.True(chosen);
        Assert.Null(name);
        Assert.Null(source);
        Assert.Null(problem);
    }

    private string Repository(string name, string? launch)
    {
        var repository = Path.Combine(_root, name);

        Directory.CreateDirectory(Path.Combine(repository, ".git"));

        if (launch is not null)
        {
            File.WriteAllText(Path.Combine(repository, ".mcp.json"), launch);
        }

        return repository;
    }

    [Fact]
    public void A_repository_is_read_from_a_folder_deep_inside_it()
    {
        var repository = Repository(
            "orders",
            """{ "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "alpha" } } } }""");

        var deep = Path.Combine(repository, "src", "deep");

        Directory.CreateDirectory(deep);

        Assert.Equal(new[] { "alpha" }, RepositoryProjects.Read(deep));
    }

    [Fact]
    public void A_launch_configuration_above_the_repository_is_never_read()
    {
        // The home directory, or a checkout this repository sits inside, must not choose a project for it.
        var outer = Path.Combine(_root, "outer");

        Directory.CreateDirectory(Path.Combine(outer, "inner", ".git"));
        File.WriteAllText(
            Path.Combine(outer, ".mcp.json"),
            """{ "mcpServers": { "parley-beta": { "env": { "PARLEY_AGENT": "orders" } } } }""");

        Assert.Empty(RepositoryProjects.Read(Path.Combine(outer, "inner")));
    }

    [Fact]
    public void The_entry_key_names_the_project_when_its_environment_does_not()
    {
        var repository = Repository("keyed", """{ "mcpServers": { "parley-beta": { "env": { "PARLEY_AGENT": "orders" } } } }""");

        Assert.Equal(new[] { "beta" }, RepositoryProjects.Read(repository));
    }

    [Fact]
    public void An_entry_connecting_by_address_names_no_project()
    {
        var repository = Repository(
            "addressed",
            """{ "mcpServers": { "parley-beta": { "env": { "PARLEY_AGENT": "orders", "PARLEY_BROKER": "127.0.0.1:1" } } } }""");

        Assert.Empty(RepositoryProjects.Read(repository));
    }

    [Fact]
    public void A_variable_reference_is_not_a_project_name()
    {
        var repository = Repository(
            "referenced",
            """{ "mcpServers": { "parley": { "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "${WHICH}" } } } }""");

        Assert.Empty(RepositoryProjects.Read(repository));
    }

    [Fact]
    public void A_repository_answering_in_two_projects_names_both()
    {
        var repository = Repository(
            "both",
            """{ "mcpServers": { "parley-alpha": { "env": { "PARLEY_AGENT": "orders" } }, "parley-beta": { "env": { "PARLEY_AGENT": "orders" } } } }""");

        Assert.Equal(new[] { "alpha", "beta" }, RepositoryProjects.Read(repository));
    }

    [Fact]
    public void Another_server_in_the_launch_configuration_names_nothing()
    {
        var repository = Repository(
            "others",
            """{ "mcpServers": { "someone-else": { "env": { "PARLEY_PROJECT": "alpha" } } } }""");

        Assert.Empty(RepositoryProjects.Read(repository));
    }

    [Fact]
    public void A_launch_configuration_that_does_not_parse_names_nothing()
    {
        // A broken file in the working directory must not stop every command from running.
        var repository = Repository("broken", "{ not json");

        Assert.Empty(RepositoryProjects.Read(repository));
    }
}
