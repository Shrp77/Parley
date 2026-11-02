using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What a failed enrolment leaves behind, and whether it says so.
/// </summary>
/// <remarks>
/// <para>
/// Other probes ask what a successful run writes. An enrolment writes three files in order, and
/// the catalogue — the one that grants a node standing consent to start the repository — is written
/// last. A failure there arrives after the launch configuration has already been replaced.
/// </para>
/// <para>
/// The exception carries the reason and nothing else, so the list of what was already written is
/// discarded with it. <i>The damage is not the failure, it is the
/// report</i>: a run that says it failed, having made the
/// repository announce itself as an agent, is telling somebody the opposite of what happened.
/// </para>
/// </remarks>
[Integration]
public sealed class ProbeTwentyFiveTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe25-" + Path.GetRandomFileName());

    public ProbeTwentyFiveTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
    }

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

    private string Workspace => Path.Combine(_root, "orders");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    /// <summary>A catalogue path that cannot be written: something else is already there.</summary>
    private string UnwritableCatalogue
    {
        get
        {
            var path = Path.Combine(_root, "node.json");

            Directory.CreateDirectory(path);

            return path;
        }
    }

    private Task<Enrolment.Outcome> EnrolAsync(string catalogue) =>
        Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, "orders", "Carts and totals.", Unattended: true)
            {
                Enroll = true,
            },
            "alpha",
            catalogue,
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task An_enrolment_that_fails_on_the_catalogue_says_the_launch_configuration_changed()
    {
        // The launch configuration is what makes a repository claim to be an agent: an MCP client
        // opening it will start a server announcing `orders` in `alpha`. If the run reports only a
        // failure, the person's reasonable reading is that nothing happened — and what has actually
        // happened is an agent that appears on a roster and that no node will ever start.
        var catalogue = UnwritableCatalogue;

        var failure = await Record.ExceptionAsync(() => EnrolAsync(catalogue));

        Assert.NotNull(failure);

        // It really did get that far.
        Assert.True(File.Exists(LaunchFile), "the launch configuration was never written");

        Assert.Contains(
            Enrolment.LaunchFile,
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_enrolment_that_succeeds_still_reports_what_it_wrote()
    {
        // The control. Without it the assertion above is satisfied by a run that reports every file
        // it might ever have touched, whether or not it did.
        var outcome = await EnrolAsync(Path.Combine(_root, "good-node.json"));

        Assert.Contains(LaunchFile, outcome.Written);
        Assert.Contains(Path.Combine(_root, "good-node.json"), outcome.Written);
    }

    [Fact]
    public async Task Nothing_is_half_written_inside_the_launch_configuration_itself()
    {
        // The narrower half of the same worry, and the one that is testable rather than argued: the
        // file this replaces holds other people's servers, so a write that left it truncated would
        // destroy configuration this program did not put there. Whatever else a failure does, the
        // file it already wrote has to be a whole document.
        await File.WriteAllTextAsync(
            LaunchFile,
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["github"] = new JsonObject { ["command"] = "npx" },
                },
            }.ToJsonString(),
            TestContext.Current.CancellationToken);

        await Record.ExceptionAsync(() => EnrolAsync(UnwritableCatalogue));

        var after = JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken));

        Assert.NotNull(after);
        Assert.NotNull(after["mcpServers"]!["github"]);
    }
}
