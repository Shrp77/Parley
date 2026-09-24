using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 2: where a write can be made to land, and how long a quoted value can be.
/// </summary>
/// <remarks>
/// <para>
/// Run 1 attacked the *values* written into a repository. This attacks the *destination*: this tooling
/// is pointed at a directory of checkouts it did not create, and decides from what it finds there
/// which files to overwrite. A checkout is somebody else's data — often a clone of somebody else's
/// repository — and `.mcp.json` is a tracked file that a repository can therefore ship as a link.
/// </para>
/// <para>
/// The second half is length. Every refusal here quotes values read from files, and a value long
/// enough to push the reason off a screen has refused nothing.
/// </para>
/// </remarks>
public sealed class ProbeThirteenTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe13-" + Path.GetRandomFileName());

    public ProbeThirteenTests() => Directory.CreateDirectory(_root);

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

    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    private static EnrollmentEntry Entry(string workspace) =>
        new(workspace, "orders", "Carts and totals.", Unattended: false)
        {
            Enroll = true,
            OwnsApproved = true,
        };

    [Fact]
    public async Task A_launch_configuration_that_is_a_link_does_not_carry_the_write_out_of_the_repository()
    {
        // **`.mcp.json` is a tracked file, so a repository can ship one as a symlink.** This tooling
        // is pointed at directories of checkouts it did not create — `parley enroll /src` — and
        // then writes into each one. A write that follows a link writes wherever the link says.
        //
        // What this asserts is deliberately the weaker of the two possible rules: the *target* must
        // not gain this tooling's content. Whether the link is refused, replaced, or written through
        // is a judgement; a clone silently rewriting a file elsewhere on the machine is not.
        var workspace = Workspace("orders");
        var outside = Path.Combine(_root, "outside.txt");

        await File.WriteAllTextAsync(outside, "not ours", TestContext.Current.CancellationToken);

        try
        {
            File.CreateSymbolicLink(Path.Combine(workspace, ".mcp.json"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
        {
            return; // No symlink privilege on this machine; the property is unobservable here.
        }

        try
        {
            await Enrolment.AddAsync(
                Entry(workspace),
                "alpha",
                Path.Combine(_root, "node.json"),
                Path.Combine(workspace, "CLAUDE.md"),
                TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Refused is a fine answer.
        }

        Assert.Equal(
            "not ours",
            await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_quoted_value_long_enough_to_bury_the_reason_is_cut()
    {
        // A refusal that scrolls its own reason off the screen has refused nothing, and every value in
        // one of these comes out of a file whose length nobody here chose.
        var refusal = Catalogues.Copied(new string('a', 4000), new string('b', 4000));

        Assert.True(
            refusal.Length < 1000,
            $"a refusal ran to {refusal.Length} characters, so its reason is off the screen");

        // The half that says what to do about it, which is the half a long value would bury.
        Assert.Contains("One agent is one checkout", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("plain")]
    public void Quoting_a_value_never_invents_one(string? value)
    {
        // The neutraliser removes; it must not add. A caller printing "'{Flat(x)}'" around a value
        // that came back longer than it went in would be quoting something the file does not say.
        var flat = Quoting.Flat(value);

        Assert.True(flat.Length <= Math.Max(value?.Length ?? 0, 0));
    }

    [Fact]
    public void A_lone_surrogate_does_not_end_the_quoting()
    {
        // Paths and roles are read as UTF-16 from JSON, which can hold an unpaired surrogate. The
        // truncation in this repository has already cut one in half once, in 008's gate.
        var flat = Quoting.Flat("orders\ud800tail");

        Assert.Contains("orders", flat, StringComparison.Ordinal);
        Assert.Contains("tail", flat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_naming_a_directory_that_is_not_a_repository_says_so()
    {
        // **The probe expected a refusal and the codebase's own rule is a warning.** Finding only
        // offers git repositories, so a directory without one arrives from somewhere else — a row a
        // surface built for itself, or a checkout that stopped being one between the finding and the
        // writing. The worktree case decided this already, in as many words: said
        // rather than refused, because "it is the person's directory and there may be a reading of it
        // I have not thought of". Refusing here would be a second rule for one situation.
        //
        // What was missing was the saying. The file lands either way, and nothing made that visible.
        var notARepository = Path.Combine(_root, "just-a-folder");

        Directory.CreateDirectory(notARepository);

        var output = new StringWriter();

        await EnrollCommand.EnrolAsync(
            [Entry(notARepository.Replace(Path.DirectorySeparatorChar, '/'))],
            describe: true,
            new Resolved(Path.Combine(_root, "node.json"), ValueSource.Argument),
            output,
            new StringWriter(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("not a git repository", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_peers_frame_is_never_appended_twice()
    {
        // Not new, and worth one assertion under this lens: the frame is appended to a file this
        // tooling does not own, and a second copy is content nobody wrote appearing in a repository.
        var workspace = Workspace("orders");
        var instructions = Path.Combine(workspace, "CLAUDE.md");

        await Enrolment.AddAsync(
            Entry(workspace), "alpha", Path.Combine(_root, "node.json"), instructions,
            TestContext.Current.CancellationToken);

        await Enrolment.AddAsync(
            Entry(workspace), "beta", Path.Combine(_root, "node.json"), instructions,
            TestContext.Current.CancellationToken);

        var text = await File.ReadAllTextAsync(instructions, TestContext.Current.CancellationToken);

        var frames = text.Split("## Peers", StringSplitOptions.None).Length - 1;

        Assert.Equal(1, frames);
    }
}
