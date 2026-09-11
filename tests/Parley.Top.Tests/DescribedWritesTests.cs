using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That every file adding an agent writes is named before it is written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by lens 2, and it was pre-existing.</b> The confirmation was titled <i>Write these two
/// files?</i> and named two — the launch configuration and the project's catalogue. Three were
/// written: enrolment also creates or appends to the repository's own <c>CLAUDE.md</c>, adding the
/// <c>## Peers</c> section that tells the agent how to use Parley.
/// </para>
/// <para>
/// This surface's rule is that nothing is written until the person has seen what would be, and it
/// matters more here than anywhere: the repository being written into is not the one they are
/// looking at.
/// </para>
/// <para>
/// <b>Checked against the disk rather than against the description.</b> A test comparing two lists
/// this surface wrote would agree with itself for ever; what this does is enrol for real and ask
/// what appeared.
/// </para>
/// </remarks>
public sealed class DescribedWritesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("parley-described-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that will not go is not a failure of the thing tested.
        }
    }

    [Fact]
    public async Task Nothing_appears_in_the_repository_that_the_confirmation_did_not_name()
    {
        var workspace = Path.Combine(_root, "orders");

        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));

        var token = TestContext.Current.CancellationToken;
        var manifest = Path.Combine(_root, "projects.json");
        var catalogue = Path.Combine(_root, "alpha-node.json");

        ProjectManifest.Write(
            manifest,
            [new ProjectSettings(
                ProjectName.Create("alpha"), Path.Combine(_root, "alpha.db"),
                "http://127.0.0.1:1", catalogue, null)]);

        await File.WriteAllTextAsync(catalogue, """{ "agents": {} }""", token);

        var before = Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).ToHashSet();
        var described = Adding.Describe(workspace, catalogue, "alpha", already: null);

        var entry = new EnrollmentEntry(workspace, "orders", "the orders service", Unattended: true)
        {
            Enroll = true,
            OwnsApproved = true,
        };

        _ = await Enrolment.AddAsync(
            entry, "alpha", catalogue, Path.Combine(workspace, Adding.Instructions),
            token, null, manifest);

        var appeared = Directory.GetFiles(workspace, "*", SearchOption.AllDirectories)
            .Except(before)
            .Select(Path.GetFileName)
            .ToList();

        // Non-vacuous first: if enrolment stopped writing into the repository at all, the check
        // below would pass while testing nothing.
        Assert.NotEmpty(appeared);

        foreach (var file in appeared)
        {
            Assert.Contains(file!, described, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_title_does_not_promise_a_count_it_cannot_keep()
    {
        // Two files or three, depending on whether another project already starts the repository.
        // A title carrying the number is one that falls out of step with the branch below it.
        Assert.DoesNotContain("two", Adding.ConfirmTitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("three", Adding.ConfirmTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_instructions_file_is_named_whichever_shape_the_enrolment_takes()
    {
        // It is written on both paths, so it is described on both. The catalogue is the only line
        // that comes and goes.
        foreach (var already in new Enrolment.Shared?[] { null, new("alpha", "orders") })
        {
            Assert.Contains(
                Adding.Instructions,
                Adding.Describe("C:/work/orders", "C:/store/beta-node.json", "beta", already),
                StringComparison.Ordinal);
        }
    }
}
