using System.Text.Json;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// One checkout is one agent, in however many projects, at however many paths it has been.
/// </summary>
/// <remarks>
/// <para>
/// <b>This replaces <c>CatalogueUniquenessTests</c>, which tested the rule 018 deleted.</b> 009's FR-013
/// let a repository answer in several projects and be started unattended by only one, because a catalogue
/// belonged to a project and two of them consenting was two nodes launching one working directory. The
/// catalogue is one file keyed by the agent's identity now, so the second consent cannot be written down
/// and the scan that used to look for it is gone.
/// </para>
/// <para>
/// <b>What is worth testing is therefore the opposite of what was.</b> The old file asserted that a second
/// project was refused; these assert that it is admitted and changes one thing — the name the repository
/// answers to there. The two facts that make that safe are the other two tests: an identity survives a
/// move, so nothing has to be repaired after one, and a copy is refused, because a copy is the one way
/// left to point one identity at two working directories.
/// </para>
/// </remarks>
public sealed class EnrollingTwiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-twice-" + Path.GetRandomFileName());

    public EnrollingTwiceTests() => Directory.CreateDirectory(_root);

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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>This machine's one catalogue.</summary>
    private string Catalogue => Path.Combine(_root, "node.json");

    /// <summary>Makes a directory that looks like a checkout.</summary>
    /// <param name="name">Its folder name.</param>
    /// <returns>Its path.</returns>
    private string Checkout(string name)
    {
        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    /// <summary>Enrols one checkout into one project.</summary>
    /// <param name="workspace">The checkout.</param>
    /// <param name="role">What that project will call it.</param>
    /// <param name="project">The project.</param>
    /// <returns>What was written and said.</returns>
    private Task<Enrolment.Outcome> EnrolAsync(string workspace, string role, string project) =>
        Enrolment.AddAsync(
            new EnrollmentEntry(workspace, role, "What is on hand.", Unattended: true)
            {
                Enroll = true,
                OwnsApproved = true,
            },
            project,
            Catalogue,
            Path.Combine(workspace, "CLAUDE.md"),
            Ct);

    /// <summary>Every agent this machine has been told it can start.</summary>
    /// <returns>One entry per identity, with the path it is catalogued at.</returns>
    private IReadOnlyDictionary<string, string?> Catalogued()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Catalogue));

        return document.RootElement.GetProperty("agents").EnumerateObject().ToDictionary(
            entry => entry.Name,
            entry => entry.Value.TryGetProperty("workspace", out var workspace)
                ? workspace.GetString()
                : null,
            StringComparer.Ordinal);
    }

    /// <summary>Which agent a checkout says it is.</summary>
    /// <param name="workspace">The checkout.</param>
    /// <returns>Its identity.</returns>
    private static AgentIdentity IdentityOf(string workspace)
    {
        Assert.True(LaunchConfiguration.TryRead(workspace, out var declared, out var problem), problem);

        return declared.Identity;
    }

    [Fact]
    public async Task A_second_project_adds_a_name_and_nothing_else()
    {
        // **The defect this feature starts from, asserted from the writing end.** One checkout answering
        // in two projects used to need an entry in each project's catalogue, and FR-013 refused the
        // second — so it was startable in one and unstartable in the other, and a request to the name it
        // answers to in the other was accepted, queued, flagged, and never delivered.
        var workspace = Checkout("inventory");

        await EnrolAsync(workspace, "stock", "alpha");

        var identity = IdentityOf(workspace);

        Assert.Equal([identity.ToString()], Catalogued().Keys);

        await EnrolAsync(workspace, "inventory", "beta");

        // One entry, unmoved: the second project changed what the repository is called there and nothing
        // about whether this machine can start it.
        Assert.Equal([identity.ToString()], Catalogued().Keys);
        Assert.Equal(identity, IdentityOf(workspace));

        // And it answers to both names, each in its own project, which is what a node announces from.
        Assert.True(LaunchConfiguration.TryRead(workspace, out var declared, out _));

        Assert.Equal(
            [("alpha", "stock"), ("beta", "inventory")],
            declared.Memberships
                .Select(membership => (membership.Project, membership.Name))
                .OrderBy(pair => pair.Project, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_move_keeps_the_identifier_and_repoints_the_entry()
    {
        // **The dividend R1 exists for.** The identity is in the repository rather than derived from its
        // path, so a checkout that moves is the same agent afterwards — the conversations it is in, the
        // work it owes and the name every project calls it by all survive, and the only thing that needs
        // saying is where it is now.
        var was = Checkout("inventory");

        await EnrolAsync(was, "stock", "alpha");

        var identity = IdentityOf(was);
        var now = Path.Combine(_root, "moved");

        Directory.Move(was, now);

        await EnrolAsync(now, "stock", "alpha");

        var catalogued = Catalogued();

        Assert.Equal([identity.ToString()], catalogued.Keys);
        Assert.Equal(now, catalogued[identity.ToString()]);
        Assert.Equal(identity, IdentityOf(now));
    }

    [Fact]
    public async Task A_copy_is_refused_and_the_refusal_names_both_paths()
    {
        // **The one way left to point one identity at two working directories**, and the reason it is
        // refused rather than resolved: the two are a move and a copy from the files, they want opposite
        // repairs, and only the person who made the second one knows which they meant. A launch
        // configuration is frequently committed, so the identity arrives in the copy by the ordinary route
        // rather than by anybody doing anything strange.
        var original = Checkout("inventory");

        await EnrolAsync(original, "stock", "alpha");

        var copy = Checkout("inventory-copy");

        File.Copy(
            Path.Combine(original, LaunchConfiguration.FileName),
            Path.Combine(copy, LaunchConfiguration.FileName));

        var refusal = await Assert.ThrowsAsync<InvalidDataException>(
            () => EnrolAsync(copy, "stock", "beta"));

        Assert.Contains(original, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(copy, refusal.Message, StringComparison.Ordinal);

        // Nothing was written, including the launch entry that would have been fine on its own: a
        // half-applied enrolment is the state nobody can read afterwards.
        Assert.Equal([IdentityOf(original).ToString()], Catalogued().Keys);
        Assert.Equal(original, Catalogued()[IdentityOf(original).ToString()]);
    }

    [Fact]
    public async Task A_second_checkout_that_re_mints_is_a_second_agent()
    {
        // The deliberate act the refusal points at. Somebody who genuinely wants two checkouts as two
        // agents takes the identity out, and gets a second one — which is what it should cost.
        var original = Checkout("inventory");

        await EnrolAsync(original, "stock", "alpha");

        var second = Checkout("inventory-two");

        await EnrolAsync(second, "spares", "alpha");

        Assert.NotEqual(IdentityOf(original), IdentityOf(second));
        Assert.Equal(2, Catalogued().Count);
    }
}
