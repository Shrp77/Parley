using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 9: how FR-013 fails.
/// </summary>
/// <remarks>
/// <para>
/// Every earlier run asked what this feature <i>writes</i>. FR-013 is the one thing it <i>refuses</i>,
/// and a refusal is only worth what its failure modes are worth: it is the whole of what stands
/// between a machine and two nodes starting one working tree with nobody watching.
/// </para>
/// <para>
/// The guard's answer is computed by reading every <b>other</b> project's catalogue — files this run
/// does not hold, did not write, and may not be able to open at all. So the question no run has asked
/// is what the guard says when it could not look: a half-saved file, or one another writer is holding
/// exclusively, which is what an enrolment or a withdrawal in that project looks like from here.
/// </para>
/// </remarks>
public sealed class ProbeTwentyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe20-" + Path.GetRandomFileName());

    public ProbeTwentyTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

        ProjectManifest.Write(
            ManifestPath,
            [
                new ProjectSettings(Name("alpha"), Store("alpha"), "http://127.0.0.1:7000", AlphaCatalogue, null),
                new ProjectSettings(Name("beta"), Store("beta"), "http://127.0.0.1:7001", BetaCatalogue, null),
            ]);
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

    private string ManifestPath => Path.Combine(_root, "projects.json");

    private string AlphaCatalogue => Path.Combine(_root, "alpha-node.json");

    private string BetaCatalogue => Path.Combine(_root, "beta-node.json");

    private static ProjectName Name(string value) =>
        ProjectName.TryCreate(value, out var name) ? name : throw new InvalidOperationException(value);

    private string Store(string project) => Path.Combine(_root, $"{project}.db");

    private IReadOnlyList<ProjectSettings> Deployment() =>
        ProjectManifest.TryRead(ManifestPath, out var projects, out _)
            ? projects
            : throw new InvalidOperationException("the probe's own manifest did not read back");

    /// <summary>Beta's catalogue, naming this workspace under <paramref name="spelling"/>.</summary>
    private string BetaCataloguing(string spelling) =>
        $$"""
          { "agents": { "orders": { "command": "claude", "workspace": {{System.Text.Json.JsonSerializer.Serialize(spelling)}} } } }
          """;

    private Task<Enrolment.Outcome> EnrolIntoAlphaAsync() =>
        Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, "orders", "Carts and totals.", Unattended: true)
            {
                Enroll = true,
                OwnsApproved = true,
            },
            "alpha",
            AlphaCatalogue,
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken,
            Deployment(),
            ManifestPath);

    /// <summary>What the run did about beta: refused, said something, or neither.</summary>
    private async Task<string> AlphaAsync()
    {
        try
        {
            var outcome = await EnrolIntoAlphaAsync();

            return string.Join(" | ", outcome.Notes);
        }
        catch (InvalidDataException refusal)
        {
            return refusal.Message;
        }
    }

    [Fact]
    public async Task The_guard_holds_when_the_other_catalogue_reads()
    {
        // The control. Without this the three below prove only that something is different about an
        // unreadable file, not that the readable case was ever refused.
        await File.WriteAllTextAsync(
            BetaCatalogue, BetaCataloguing(Workspace), TestContext.Current.CancellationToken);

        Assert.Contains("beta", await AlphaAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_half_saved_catalogue_is_not_silently_read_as_no_consent()
    {
        // A file caught mid-save, or one carrying a merge conflict marker. `TryReadFrom` says it
        // could not be read and hands back a reason; the guard drops the reason and moves on, so
        // "beta might already start this" and "beta does not start this" become the same answer.
        await File.WriteAllTextAsync(
            BetaCatalogue,
            BetaCataloguing(Workspace)[..40],
            TestContext.Current.CancellationToken);

        var said = await AlphaAsync();

        Assert.Contains("beta", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_catalogue_another_writer_is_holding_is_not_silently_read_as_no_consent()
    {
        // Not a hypothetical corruption: this is precisely what an enrolment or a withdrawal in beta
        // looks like from here, since both take that file with `FileShare.None` for the length of
        // their run. The machine-wide gate serialises grants against each other and cannot serialise
        // this run against a withdrawal, which does not compete for it.
        await File.WriteAllTextAsync(
            BetaCatalogue, BetaCataloguing(Workspace), TestContext.Current.CancellationToken);

        using var held = new FileStream(
            BetaCatalogue, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var said = await AlphaAsync();

        Assert.Contains("beta", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_catalogue_that_is_a_directory_is_not_silently_read_as_no_consent()
    {
        // The third route to the same skip, and the one nobody creates on purpose: a path in the
        // manifest that names something other than a file. FR-013 asks a question about the machine
        // and answers it from files it may not be able to open, so every way of not opening one has
        // to lead somewhere other than "no".
        Directory.CreateDirectory(BetaCatalogue);

        Assert.Contains("beta", await AlphaAsync(), StringComparison.Ordinal);
    }
}
