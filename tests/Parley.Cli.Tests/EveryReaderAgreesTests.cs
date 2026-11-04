using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Parley.Tests;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>Whatever reads or writes the file reaches the same answer about it (SPEC.md §4.1.2).</summary>
/// <remarks>
/// A reader that shrugged off a section another refuses would disagree about which agents exist. The adapter's half is
/// <c>MachineBlockOptionsTests</c>, in the project that can see it.
/// </remarks>
[Integration]
public sealed class EveryReaderAgreesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("parley-agree-").FullName;

    /// <summary>The table's names, which are what a failure reports.</summary>
    public static TheoryData<string> Cases => [.. LaunchTable.Cases.Select(c => c.Name)];

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

    private string Written(LaunchCase theCase)
    {
        File.WriteAllText(Path.Combine(_root, ".mcp.json"), theCase.File);

        return _root;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Reading_the_folder_says_what_the_table_says(string name)
    {
        var theCase = LaunchTable.Cases.Single(c => c.Name == name);
        var read = LaunchConfiguration.TryRead(Written(theCase), out _, out var problem);

        Assert.Equal(theCase.Problem is null, read);

        if (theCase.Problem is { } carried)
        {
            Assert.Contains(carried, problem, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_launch_finds_what_the_table_says(string name)
    {
        var theCase = LaunchTable.Cases.Single(c => c.Name == name);
        var located = LocatedLaunch.Find(null, "infra", "acme", Written(theCase));

        if (theCase.Problem is { } carried)
        {
            Assert.Contains(carried, located?.Problem, StringComparison.Ordinal);

            return;
        }

        Assert.Null(located!.Problem);

        var settings = located.SettingsFor(theCase.Machine, _ => null);

        if (theCase.Key == "parley-acme")
        {
            Assert.Equal(theCase.Owns, settings.Owns);
            Assert.Equal(theCase.Broker, settings.Broker);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Enrolling_refuses_what_the_table_refuses(string name)
    {
        var theCase = LaunchTable.Cases.Single(c => c.Name == name);

        var attempt = () => EnrollmentPlan.Describe(
            new EnrollmentEntry(_root, "infra", null, Unattended: false), theCase.File, null, false, "acme");

        if (theCase.Problem is { } carried)
        {
            Assert.Contains(carried, Assert.Throws<InvalidDataException>(attempt).Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotNull(attempt());
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Writing_a_line_refuses_what_the_table_refuses(string name)
    {
        var theCase = LaunchTable.Cases.Single(c => c.Name == name);

        var refused = await OwnershipLine.WriteAsync(
            Written(theCase),
            "acme",
            AgentName.Create("infra@" + MachineName.Current),
            "A line.",
            TestContext.Current.CancellationToken);

        if (theCase.Problem is { } carried)
        {
            Assert.Contains(carried, refused, StringComparison.Ordinal);
            Assert.Equal(theCase.File, File.ReadAllText(Path.Combine(_root, ".mcp.json")));
        }
        else
        {
            Assert.Null(refused);
        }
    }

    [Fact]
    public void A_node_reports_a_broken_section_and_announces_the_others()
    {
        var bad = Path.Combine(_root, "bad");
        var good = Path.Combine(_root, "good");

        Directory.CreateDirectory(bad);
        Directory.CreateDirectory(good);

        File.WriteAllText(
            Path.Combine(bad, ".mcp.json"),
            LaunchTable.Cases.Single(c => c.Name == "a role in a block").File);
        File.WriteAllText(
            Path.Combine(good, ".mcp.json"),
            """{ "mcpServers": { "parley-acme": { "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "acme" } } } }""");

        var file = Path.Combine(_root, "node.json");

        File.WriteAllText(
            file,
            new System.Text.Json.Nodes.JsonObject
            {
                ["agents"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011"] = new System.Text.Json.Nodes.JsonObject { ["command"] = "x", ["workspace"] = bad },
                    ["1f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5012"] = new System.Text.Json.Nodes.JsonObject { ["command"] = "x", ["workspace"] = good },
                },
            }.ToJsonString());

        var catalogue = NodeCatalogue.ReadFrom(file);
        var workspaces = catalogue.Workspaces();

        Assert.Contains("PARLEY_AGENT", workspaces.Single(w => w.Workspace == bad).Problem, StringComparison.Ordinal);
        Assert.Contains("orders", catalogue.Announceable("acme").Names);
    }
}
