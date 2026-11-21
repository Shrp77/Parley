using System.Text.Json.Nodes;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>A path under a folder that is a link to a share is judged without anything on it being looked up.</summary>
/// <remarks>
/// <para>
/// <c>File.Exists</c> and <c>Directory.Exists</c> read the last part of a path as itself, so a test whose last part is the
/// link proves nothing about them; a part above it is looked up through the link, and Windows offers the person's sign-in
/// to the host. Each guard here passed every other test with the old call put back (review, lens 3 round 13).
/// </para>
/// <para>
/// The share is a TEST-NET host, which never answers: a lookup that went there takes the SMB wait, about 21 seconds.
/// </para>
/// </remarks>
[Integration]
public sealed class NothingUnderALinkToAShareIsLookedUpTests : IDisposable
{
    private static readonly TimeSpan Looking = TimeSpan.FromSeconds(4);

    private readonly string _root = Directory.CreateTempSubdirectory("parley-under-share").FullName;

    private string Link => Path.Combine(_root, "lnk");

    private string UnderIt => Path.Combine(Link, "repo");

    public void Dispose()
    {
        if (new DirectoryInfo(Link).LinkTarget is not null)
        {
            Directory.Delete(Link);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void LinkToAShare()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "links to shares are a Windows sign-in");

        try
        {
            Directory.CreateSymbolicLink(Link, $@"\\192.0.2.{Random.Shared.Next(2, 254)}\share");
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("symbolic links need Developer Mode here");
        }
    }

    private static async Task QuicklyAsync(Func<Task> act)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await act();
        }
        catch (Exception refused) when (refused is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A refusal is fine; only the time it took is the question.
        }

        Assert.True(timer.Elapsed < Looking, $"took {timer.Elapsed}: a path under the link was looked up");
    }

    [Fact]
    public async Task Enrolment_validates_such_a_workspace_without_looking()
    {
        LinkToAShare();

        await QuicklyAsync(() => EnrollCommand.EnrolAsync(
            [new EnrollmentEntry(UnderIt, "orders", null, Unattended: true) { Enroll = true }],
            describe: true,
            new Resolved(Path.Combine(_root, "node.json"), ValueSource.Argument),
            TextWriter.Null,
            TextWriter.Null,
            cancellationToken: Ct));

        await QuicklyAsync(() => Enrolment.AddAsync(
            new EnrollmentEntry(UnderIt, "orders", null, Unattended: true) { Enroll = true },
            "acme", Path.Combine(_root, "node.json"), Path.Combine(_root, "CLAUDE.md"), Ct));
    }

    [Fact]
    public async Task A_scan_of_such_a_root_does_not_look()
    {
        LinkToAShare();

        await QuicklyAsync(() => EnrollCommand.RunAsync(
            [UnderIt], selectAll: true, new Resolved(Path.Combine(_root, "node.json"), ValueSource.Argument),
            TextWriter.Null, TextWriter.Null, Ct, describe: true));
    }

    [Fact]
    public async Task A_catalogue_line_naming_such_a_folder_is_judged_without_looking()
    {
        // A repository whose identity the catalogue already places under the link: the copy check asks whether that
        // folder is still there.
        LinkToAShare();
        var repository = Directory.CreateDirectory(Path.Combine(_root, "orders")).FullName;
        var identity = AgentIdentity.Mint();

        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        await File.WriteAllTextAsync(
            Path.Combine(repository, ".mcp.json"),
            new JsonObject { [LaunchConfiguration.IdentityMember] = identity.ToString() }.ToJsonString(),
            Ct);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "node.json"),
            new JsonObject
            {
                ["agents"] = new JsonObject
                {
                    [identity.ToString()] = new JsonObject { ["command"] = "claude", ["workspace"] = UnderIt },
                },
            }.ToJsonString(),
            Ct);

        await QuicklyAsync(() => Enrolment.AddAsync(
            new EnrollmentEntry(repository, "orders", null, Unattended: true) { Enroll = true },
            "acme", Path.Combine(_root, "node.json"), Path.Combine(repository, "CLAUDE.md"), Ct));
    }

    [Fact]
    public async Task The_instructions_file_of_such_a_workspace_is_chosen_without_looking()
    {
        LinkToAShare();

        await QuicklyAsync(() =>
        {
            _ = EnrollCommand.InstructionsFile(UnderIt);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_file_is_never_created_under_such_a_folder()
    {
        LinkToAShare();

        await QuicklyAsync(() =>
        {
            using var created = LocalOpen.Create(Path.Combine(UnderIt, ".mcp.json"));

            return Task.CompletedTask;
        });

        // The first enrolment's own write, which creates the launch configuration when the hold found none.
        await QuicklyAsync(() => Enrolment.OverwriteHeldAsync(null, Path.Combine(UnderIt, ".mcp.json"), "{}", Ct));
    }
}
