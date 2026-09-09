using Parley.Setup;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 1, run 6: the paths the earlier fixes did not reach.
/// </summary>
/// <remarks>
/// <para>
/// A fix is a claim about every caller, and 1.1 and 1.2 were each applied to the callers the probe
/// that found them happened to run. This asks after the ones it did not: the reader that goes through
/// <c>git show</c> rather than the working tree, and the bulk writer that has its own write path
/// rather than sharing the one that was made exclusive.
/// </para>
/// <para>
/// The pattern is worth naming because this repository has met it before — a rule applied to half the
/// output is worse than either applied whole.
/// </para>
/// </remarks>
public sealed class ProbeSixTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe6-" + Path.GetRandomFileName());

    public ProbeSixTests() => Directory.CreateDirectory(Checkouts);

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

    private const char Backslash = '\\';

    private const string Duplicated = """
        {"mcpServers":{
          "parley":{"command":"parley-mcp","env":{"PARLEY_AGENT":"orders","PARLEY_OWNS":"Carts."}},
          "parley":{"command":"parley-mcp","env":{"PARLEY_AGENT":"orders","PARLEY_OWNS":"Carts."}}
        }}
        """;

    [Fact]
    public async Task A_repository_whose_history_holds_a_duplicated_member_does_not_end_the_scan()
    {
        // **Where 1.1's fix did not reach.** The working-tree reader learned to handle the refusal;
        // the reader that recovers an earlier `owns` line through `git show` has its own catch list,
        // and a repository whose *history* holds a duplicated member is exactly as likely as one
        // whose working tree does — more so, since the history is where the careless merge landed.
        var repository = Path.Combine(Checkouts, "orders");

        Directory.CreateDirectory(repository);

        if (!Git(repository, "init") || !Git(repository, "config user.email a@b.c")
            || !Git(repository, "config user.name Test"))
        {
            return; // No usable git on this machine; the evidence this reads is optional by design.
        }

        await File.WriteAllTextAsync(
            Path.Combine(repository, ".mcp.json"), Duplicated, TestContext.Current.CancellationToken);

        Git(repository, "add .mcp.json");
        Git(repository, "commit -m registered");
        File.Delete(Path.Combine(repository, ".mcp.json"));

        var output = new StringWriter();
        var error = new StringWriter();

        var code = await EnrollCommand.RunAsync(
            ["scan", Checkouts, ManifestPath],
            selectAll: false,
            new Resolved(Path.Combine(_root, "node.json"), ValueSource.Argument),
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.True(File.Exists(ManifestPath), "the scan did not finish");
    }

    [Fact]
    public async Task Six_bulk_runs_sharing_one_catalogue_lose_none_of_their_agents()
    {
        // **Where 1.2 and 1.3 did not reach.** `enroll apply` does not go through `AddAsync`: it
        // plans, calls `Enrolment.WriteAsync`, and saves the catalogue itself — three write paths
        // that never took the holds the single-repository path was given.
        //
        // Six runs over one repository would have written identical content and hidden the loss, so
        // this gives each run its own repository and one shared catalogue: the file every one of them
        // must add a line to. That is a machine being set up by more than one person, or by a script
        // and a person at once.
        var catalogue = Path.Combine(_root, "shared-node.json");

        var runs = Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            var repository = Path.Combine(Checkouts, $"repo{i}");

            Directory.CreateDirectory(repository);
            Directory.CreateDirectory(Path.Combine(repository, ".git"));

            var manifest = Path.Combine(_root, $"enrol-{i}.json");
            var slashed = repository.Replace(Backslash, '/');
            var catalogueSlashed = catalogue.Replace(Backslash, '/');

            await File.WriteAllTextAsync(
                manifest,
                $$"""
                  {
                    "node_config": "{{catalogueSlashed}}",
                    "agents": [ {
                      "workspace": "{{slashed}}",
                      "agent": "agent{{i}}",
                      "owns": "What it owns. Not what it does not.",
                      "unattended": true,
                      "enroll": true,
                      "owns_approved": true
                    } ]
                  }
                  """,
                TestContext.Current.CancellationToken);

            try
            {
                var code = await EnrollCommand.RunAsync(
                    ["apply", manifest],
                    selectAll: null,
                    new Resolved(catalogue, ValueSource.Argument),
                    new StringWriter(),
                    new StringWriter(),
                    TestContext.Current.CancellationToken);

                return code == 0 ? $"agent{i}" : null;
            }
            catch (IOException)
            {
                return null;
            }
        })).ToArray();

        var accepted = (await Task.WhenAll(runs)).Where(name => name is not null).ToArray();

        var listed = JsonNode.Parse(
            await File.ReadAllTextAsync(catalogue, TestContext.Current.CancellationToken))?["agents"]
            as JsonObject;

        foreach (var agent in accepted)
        {
            Assert.True(
                listed?.ContainsKey(agent!) == true,
                $"'{agent}' was told its enrolment succeeded and is not in the catalogue "
                + $"({listed?.Count ?? 0} of {accepted.Length} accepted are there)");
        }
    }

    private static bool Git(string workspace, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(20_000);

            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
