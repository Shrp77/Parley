using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// A command's project taken from its context, and `parley use`, through the real tool.
/// </summary>
/// <remarks>
/// Nothing listens at either project's address, so a command that chose a project reaches the transport and names the
/// address it could not reach — which is how the test sees which project was chosen, beside the line on standard error
/// saying which and why. A machine naming two projects, because that is where the choice exists at all.
/// </remarks>
public sealed class ProjectContextCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-use-" + Guid.NewGuid().ToString("N"));

    public ProjectContextCommandTests()
    {
        Directory.CreateDirectory(_root);

        ProjectManifest.Write(
            Root,
            [
                new ProjectSettings(ProjectName.Create("alpha"), Path.Combine(_root, "a.db"), "http://127.0.0.1:2", null),
                new ProjectSettings(ProjectName.Create("beta"), Path.Combine(_root, "b.db"), "http://127.0.0.1:3", null),
            ]);
    }

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

    // **The root rather than the manifest file.** `--config` and PARLEY_CONFIG name the directory
    // this machine keeps its Parley files in; the projects are the folders under it.
    private string Root => _root;

    private (string Name, string Value)[] Environment(string? terminalProject = null) =>
        terminalProject is null
            ? [(ProjectManifest.PathVariable, Root)]
            : [(ProjectManifest.PathVariable, Root), (EnrollmentPlan.ProjectVariable, terminalProject)];

    [Fact]
    public void A_terminals_project_is_the_one_a_command_acts_on_and_it_says_so()
    {
        var (code, _, error) = ToolProcess.RunFullyIn(_root, Environment("alpha"), "registry");

        Assert.Equal(69, code);
        Assert.Contains("127.0.0.1:2", error, StringComparison.Ordinal);
        Assert.Contains("Project: alpha (from PARLEY_PROJECT).", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_repository_a_command_runs_in_names_its_project()
    {
        var repository = Path.Combine(_root, "orders");
        var inside = Path.Combine(repository, "src");

        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(inside);
        File.WriteAllText(
            Path.Combine(repository, ".mcp.json"),
            """{ "mcpServers": { "parley-beta": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "beta" } } } }""");

        var (code, _, error) = ToolProcess.RunFullyIn(inside, Environment(), "registry");

        Assert.Equal(69, code);
        Assert.Contains("127.0.0.1:3", error, StringComparison.Ordinal);
        Assert.Contains("Project: beta (from this repository's .mcp.json).", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_flag_still_wins_over_the_terminal_and_says_so()
    {
        var (code, _, error) = ToolProcess.RunFullyIn(_root, Environment("alpha"), "--project", "beta", "registry");

        Assert.Equal(69, code);
        Assert.Contains("127.0.0.1:3", error, StringComparison.Ordinal);
        Assert.Contains("Project: beta (named with --project).", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Use_prints_the_line_each_shell_needs()
    {
        var (code, output, _) = ToolProcess.RunFullyIn(_root, Environment(), "use", "alpha");

        Assert.Equal(0, code);
        Assert.Contains("$env:PARLEY_PROJECT = 'alpha'", output, StringComparison.Ordinal);
        Assert.Contains("export PARLEY_PROJECT=alpha", output, StringComparison.Ordinal);
        Assert.Contains("set PARLEY_PROJECT=alpha", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powershell", "$env:PARLEY_PROJECT = 'alpha'")]
    [InlineData("posix", "export PARLEY_PROJECT=alpha")]
    [InlineData("cmd", "set PARLEY_PROJECT=alpha")]
    public void Use_with_a_shell_prints_that_line_alone_for_the_shell_to_run(string shell, string line)
    {
        var (code, output, _) = ToolProcess.RunFullyIn(_root, Environment(), "use", "alpha", "--shell", shell);

        Assert.Equal(0, code);
        Assert.Equal(line, output.Trim());
    }

    [Fact]
    public void Clearing_prints_the_line_that_unsets_it()
    {
        var (code, output, _) = ToolProcess.RunFullyIn(_root, Environment(), "use", "--clear", "--shell", "posix");

        Assert.Equal(0, code);
        Assert.Equal("unset PARLEY_PROJECT", output.Trim());
    }

    [Fact]
    public void Use_refuses_a_project_this_machine_does_not_describe()
    {
        var (code, _, error) = ToolProcess.RunFullyIn(_root, Environment(), "use", "gamma");

        Assert.Equal(78, code);
        Assert.Contains("alpha, beta", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Use_with_nothing_says_which_project_is_in_use_and_what_chose_it()
    {
        var (code, output, _) = ToolProcess.RunFullyIn(_root, Environment("beta"), "use");

        Assert.Equal(0, code);
        Assert.Contains("beta (from PARLEY_PROJECT).", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shell_option_belongs_to_use_alone()
    {
        var (code, _, error) = ToolProcess.RunFullyIn(_root, Environment("alpha"), "registry", "--shell", "posix");

        Assert.Equal(64, code);
        Assert.Contains("only for `parley use`", error, StringComparison.Ordinal);
    }
}
