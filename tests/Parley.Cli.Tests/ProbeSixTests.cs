using Parley.Configuration;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// The paths the other guards do not reach.
/// </summary>
/// <remarks>
/// <para>
/// A fix is a claim about every caller, and 1.1 and 1.2 were each applied to the callers the probe
/// that found them happened to run. This asks after the ones it did not: the bulk writer that has its
/// own write path rather than sharing the one that was made exclusive. It also probed a reader that went
/// through <c>git show</c>, which the scan no longer has.
/// </para>
/// <para>
/// The pattern is worth naming because this repository has met it before — a rule applied to half the
/// output is worse than either applied whole.
/// </para>
/// </remarks>
[Integration]
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

    // **The scan does not read history**, so there is no probe of a duplicated member in a committed
    // .mcp.json ending the scan through `git show`; a probe that cannot fail is worse than none.

    [Fact]
    public async Task Six_bulk_runs_sharing_one_catalogue_lose_none_of_their_agents()
    {
        // **Where 1.2 and 1.3 did not reach.** The bulk enrolment does not go through `AddAsync`: it
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

            // The rows a surface hands back, built here as the picker would: forward-slashed
            // workspace, ticked, and consenting to an unattended start so each run has a line to
            // add to the shared catalogue.
            var chosen = new EnrollmentEntry(
                repository.Replace('\\', '/'),
                $"agent{i}",
                "What it owns. Not what it does not.",
                Unattended: true)
            {
                Enroll = true,
            };

            try
            {
                var code = await EnrollCommand.EnrolAsync(
                    [chosen],
                    describe: false,
                    new Resolved(catalogue, ValueSource.Argument),
                    new StringWriter(),
                    new StringWriter(),
                    cancellationToken: TestContext.Current.CancellationToken);

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
            // **Looked up by the identity its own checkout declares** (018). The catalogue is keyed by
            // that rather than by the role, and the repository is where it is written — so this asks the
            // same question through the same two files a node would, and a run that wrote one file and
            // not the other fails here rather than passing on a key that matches nothing.
            var repository = Path.Combine(Checkouts, $"repo{agent!["agent".Length..]}");

            Assert.True(
                LaunchConfiguration.TryRead(repository, out var declared, out var problem), problem);

            Assert.True(
                declared.Identity.Known,
                $"'{agent}' was told its enrolment succeeded and its repository declares no identity");

            Assert.True(
                listed?.ContainsKey(declared.Identity.ToString()) == true,
                $"'{agent}' was told its enrolment succeeded and is not in the catalogue "
                + $"({listed?.Count ?? 0} of {accepted.Length} accepted are there)");
        }
    }
}
