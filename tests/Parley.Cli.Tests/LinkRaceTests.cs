using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>An agent flipping its own folder or launch configuration to a link to a share never gets it looked up.</summary>
/// <remarks>
/// <para>
/// The walk read every part of the path and refused a link off this machine, and an ordinary open afterwards followed
/// whatever stood there by then. A process flipping the folder, or <c>.mcp.json</c>, between a real one and a link to a
/// share won that gap within seconds, and Windows offered the person's sign-in to the host (review, lens 2 round 13).
/// </para>
/// <para>
/// The share is a TEST-NET host, which never answers: an open that followed the link takes the SMB wait, about 21
/// seconds, so a call that takes longer than a few is one that went looking. A fresh host each run, because Windows
/// answers a host it failed to reach a moment ago at once, which would hide a lookup.
/// </para>
/// </remarks>
[Integration]
public sealed class LinkRaceTests : IDisposable
{
    private static readonly TimeSpan Flipping = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Looking = TimeSpan.FromSeconds(4);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-link-race-" + Guid.NewGuid().ToString("N"));
    private readonly string _share = $@"\\192.0.2.{Random.Shared.Next(2, 254)}\share";

    public LinkRaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Links first, by exact name, so nothing is deleted through one, and found without entering any: a walk into a link
        // to the share would wait on it.
        var entries = new List<string>();

        foreach (var entry in Directory.EnumerateFileSystemEntries(_root))
        {
            entries.Add(entry);

            if (new FileInfo(entry).LinkTarget is null && Directory.Exists(entry))
            {
                entries.AddRange(Directory.EnumerateFileSystemEntries(entry));
            }
        }

        foreach (var link in entries.Where(entry => new FileInfo(entry).LinkTarget is not null).ToList())
        {
            try
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
                else
                {
                    File.Delete(link);
                }
            }
            catch (IOException)
            {
                // Best effort.
            }
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

    [Fact]
    public async Task A_folder_flipped_to_a_link_to_a_share_is_never_followed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "links to shares are a Windows sign-in");

        var repository = Path.Combine(_root, "repo");
        var aside = Path.Combine(_root, "repo-aside");

        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, ".mcp.json"), "{}", TestContext.Current.CancellationToken);

        SkipUnlessLinksCanBeMade();

        await RaceAsync(
            () =>
            {
                Directory.Move(repository, aside);
                Directory.CreateSymbolicLink(repository, _share);
                Directory.Delete(repository);
                Directory.Move(aside, repository);
            },
            () =>
            {
                var (_, stream) = LocalOpen.File(Path.Combine(repository, ".mcp.json"), FileAccess.ReadWrite, FileShare.None);

                stream?.Dispose();
                _ = LocalOpen.Folder(repository);
            });
    }

    [Fact]
    public async Task A_launch_configuration_flipped_to_a_link_to_a_share_is_never_followed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "links to shares are a Windows sign-in");

        var repository = Path.Combine(_root, "repo");
        var launch = Path.Combine(repository, ".mcp.json");
        var aside = Path.Combine(repository, "aside.json");

        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(launch, "{}", TestContext.Current.CancellationToken);

        SkipUnlessLinksCanBeMade();

        await RaceAsync(
            () =>
            {
                File.Move(launch, aside);
                File.CreateSymbolicLink(launch, _share + @"\x.json");
                File.Delete(launch);
                File.Move(aside, launch);
            },
            () =>
            {
                // Shared, so the flipper can move the file while it is open: held exclusively, the opens alone kept the
                // flipper from flipping under load, and the race hardly ran.
                var (_, stream) = LocalOpen.File(launch, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                stream?.Dispose();
            });
    }

    [Fact]
    public void A_link_whose_target_holds_a_tilde_behind_a_link_to_a_share_is_never_looked_up()
    {
        // Path.GetFullPath expands short names whenever a path holds `~`, through GetLongPathNameW, which walks the real
        // path and follows every link on it before a part has been read: no race needed (review, lens 2 round 14).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "links to shares are a Windows sign-in");

        SkipUnlessLinksCanBeMade();

        var shareLink = Path.Combine(_root, "sharelink");
        var repository = Path.Combine(_root, "repo");
        var launch = Path.Combine(_root, "file-repo", ".mcp.json");

        Directory.CreateSymbolicLink(shareLink, _share);
        Directory.CreateSymbolicLink(repository, Path.Combine(shareLink, "x~y"));
        Directory.CreateDirectory(Path.GetDirectoryName(launch)!);
        File.CreateSymbolicLink(launch, Path.Combine(shareLink, "x~y.json"));

        var timer = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(LocalOpenStatus.OffThisMachine, LocalOpen.Folder(repository));
        Assert.Equal(LocalOpenStatus.OffThisMachine, LocalOpen.File(launch, FileAccess.Read, FileShare.Read, followLink: true).Status);
        Assert.True(OffMachineLinks.LeadsOffThisMachine(repository));
        Assert.NotNull(LocalStorage.Problem(repository));
        Assert.True(timer.Elapsed < Looking, $"took {timer.Elapsed}: a short name was expanded through the link to the share");
    }

    [Fact]
    public async Task A_file_another_program_holds_is_said_to_be_there_never_read_as_absent()
    {
        // Read as no text, a held .mcp.json made a plan say *create* for a file that exists (review, lens 1 round 12).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows enforces a sharing mode on every open");
        var launch = Path.Combine(_root, ".mcp.json");

        await File.WriteAllTextAsync(launch, "{}", TestContext.Current.CancellationToken);

        using (new FileStream(launch, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => LocalOpen.ReadText(launch, json: true));
        }

        Assert.Equal("{}", LocalOpen.ReadText(launch, json: true).Text);
    }

    private void SkipUnlessLinksCanBeMade()
    {
        var probe = Path.Combine(_root, "probe");

        try
        {
            Directory.CreateSymbolicLink(probe, _root);
            Directory.Delete(probe);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("symbolic links need Developer Mode here");
        }
    }

    // Flips on one thread and opens on this one until the time is up; every open must come back before an SMB wait could.
    private static async Task RaceAsync(Action flip, Action open)
    {
        using var stop = new CancellationTokenSource(Flipping);
        var flips = 0;

        var flipper = Task.Run(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        flip();
                        flips++;
                    }
                    catch (Exception busy) when (busy is IOException or UnauthorizedAccessException)
                    {
                        // An open held it for a moment; flip again.
                    }
                }
            },
            TestContext.Current.CancellationToken);

        var opens = 0;
        var slowest = TimeSpan.Zero;

        while (!stop.IsCancellationRequested)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();

            open();
            opens++;

            slowest = timer.Elapsed > slowest ? timer.Elapsed : slowest;
            Assert.True(timer.Elapsed < Looking, $"open {opens} took {timer.Elapsed}: it followed a link to the share");
        }

        await flipper;

        Assert.True(flips > 100 && opens > 100, $"the race hardly ran: {flips} flips, {opens} opens");
    }
}
