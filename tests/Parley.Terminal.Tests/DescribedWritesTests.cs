using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Terminal.Tests;

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
[Integration]
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
        var manifest = _root;
        var catalogue = Path.Combine(_root, "alpha-node.json");

        ProjectManifest.Write(
            manifest,
            [new ProjectSettings(
                ProjectName.Create("alpha"), Path.Combine(_root, "alpha.db"),
                "http://127.0.0.1:1", null)]);

        await File.WriteAllTextAsync(catalogue, """{ "agents": {} }""", token);

        var before = Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).ToHashSet();
        var described = Adding.Describe(workspace, catalogue, "alpha");

        var entry = new EnrollmentEntry(workspace, "orders", "the orders service", Unattended: true)
        {
            Enroll = true,
        };

        _ = await Enrolment.AddAsync(
            entry, "alpha", catalogue, Path.Combine(workspace, Adding.Instructions),
            token, manifest);

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

    // **A test stood here that walked both shapes an enrolment could take, and 018 left one.** It
    // described the same repository twice — once as a first enrolment and once as one into a project that
    // already started it elsewhere — and asserted the standing-instructions file was named in both. There
    // is one shape now: the catalogue is one file per machine keyed by the agent's identity, so a second
    // project writes no catalogue line of its own and is told nothing special. What the deleted test
    // asserted of the surviving shape is asserted by `EnrolmentOrderTests` and by `AddingTests`.
}
