using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That an address given explicitly is honoured on a machine naming several projects, and a project name still is not guessed.
/// </summary>
/// <remarks>
/// <para>
/// Every client command refuses to choose between several configured projects when none is named, and that stays: acting
/// on a deployment nobody asked for produces output that looks entirely normal. What it also refused was
/// <c>--broker</c> and <c>PARLEY_BROKER</c>, which say exactly where to connect — so any caller connecting by address,
/// among them the waiting hook reading a launch configuration, could ask nothing on the machines with the most in flight.
/// Found by the sweep over project-scoped callers.
/// </para>
/// <para>
/// Nothing listens at the address, so a command that got past selection says no broker is listening (69) and one that
/// did not says the machine has several projects (78). The distinction is the whole assertion.
/// </para>
/// </remarks>
public sealed class ExplicitBrokerSelectionTests : IDisposable
{
    private const string Nowhere = "http://127.0.0.1:1";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-explicit-" + Guid.NewGuid().ToString("N"));

    public ExplicitBrokerSelectionTests()
    {
        Directory.CreateDirectory(_directory);

        ProjectManifest.Write(
            ManifestPath,
            [
                new ProjectSettings(ProjectName.Create("alpha"), Path.Combine(_directory, "a.db"), "http://127.0.0.1:2", null),
                new ProjectSettings(ProjectName.Create("beta"), Path.Combine(_directory, "b.db"), "http://127.0.0.1:3", null),
            ]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string ManifestPath => Path.Combine(_directory, "projects.json");

    [Fact]
    public void Naming_nothing_on_a_machine_with_several_projects_is_still_refused()
    {
        var (code, error) = ToolProcess.Run([(ProjectManifest.PathVariable, ManifestPath)], "registry");

        Assert.Equal(78, code);
        Assert.Contains("alpha, beta", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_given_on_the_command_line_is_where_it_connects()
    {
        var (code, error) = ToolProcess.Run([(ProjectManifest.PathVariable, ManifestPath)], "--broker", Nowhere, "waiting", "orders@test");

        Assert.Equal(69, code);
        Assert.Contains("127.0.0.1:1", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_in_the_environment_is_where_it_connects()
    {
        var (code, error) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, ManifestPath), (BrokerEndpoint.Variable, Nowhere)], "registry");

        Assert.Equal(69, code);
        Assert.Contains("127.0.0.1:1", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_the_manifest_does_not_describe_is_refused_even_with_an_address()
    {
        var (code, error) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, ManifestPath)], "--broker", Nowhere, "--project", "gamma", "registry");

        Assert.Equal(78, code);
        Assert.Contains("No project named 'gamma'", error, StringComparison.Ordinal);
    }
}
