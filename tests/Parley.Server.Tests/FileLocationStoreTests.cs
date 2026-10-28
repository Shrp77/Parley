using System.Security.Cryptography;
using Parley.Core;
using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// The file location on disk: staging, committing, holds and the folders it keeps (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The holds are what these are mostly about</b>, because a hold that released too early delivers a mixed file
/// and one that never released makes a file impossible to replace. Each is checked by ordering — what has and has
/// not completed at a moment — rather than by timing, so a slow machine cannot turn a pass into a flake.
/// </para>
/// <para>
/// The disk is real and temporary: staging and moving are filesystem facts, and a fake filesystem would test the
/// fake.
/// </para>
/// </remarks>
public sealed class FileLocationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-location-" + Guid.NewGuid().ToString("N"));
    private readonly FileLocationStore _location;

    public FileLocationStoreTests()
    {
        _location = new FileLocationStore(_root, _root + "-incoming", deleteOnDispose: true);
        _location.Prepare();
    }

    public void Dispose() => _location.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SharedFileName Notes = SharedFileName.Create("price-snapshot.md");

    [Fact]
    public async Task A_hundred_megabytes_streamed_in_pieces_arrives_byte_identical_under_the_hash_it_was_written_with()
    {
        const int Pieces = 400;
        var piece = new byte[FileLimits.ChunkBytes];
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using (var staged = _location.Stage())
        {
            for (var i = 0; i < Pieces; i++)
            {
                Array.Fill(piece, (byte)(i % 251));
                expected.AppendData(piece);
                await staged.WriteAsync(piece, Ct);
            }

            var hash = await staged.FinishAsync(Ct);

            Assert.Equal(ContentHash.FromDigest(expected.GetHashAndReset()), hash);
            Assert.Equal((long)Pieces * FileLimits.ChunkBytes, staged.SizeBytes);

            staged.CommitTo("pricing", Notes);
        }

        var read = await FileLocationStore.HashAsync(_location.FilePath("pricing", Notes), Ct);

        Assert.Equal((long)Pieces * FileLimits.ChunkBytes, read!.Value.SizeBytes);

        using var again = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < Pieces; i++)
        {
            Array.Fill(piece, (byte)(i % 251));
            again.AppendData(piece);
        }

        Assert.Equal(ContentHash.FromDigest(again.GetHashAndReset()), read.Value.Sha256);
        Assert.Empty(Directory.EnumerateFiles(_location.Incoming));
    }

    [Fact]
    public async Task A_placement_that_never_finishes_leaves_nothing_in_the_folder_or_in_staging()
    {
        await using (var staged = _location.Stage())
        {
            await staged.WriteAsync("half"u8.ToArray(), Ct);
        }

        Assert.False(File.Exists(_location.FilePath("pricing", Notes)));
        Assert.Empty(Directory.EnumerateFiles(_location.Incoming));
    }

    [Fact]
    public async Task Staging_is_beside_the_location_and_never_inside_it()
    {
        await using var staged = _location.Stage();

        Assert.False(staged.Path.StartsWith(_location.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith(_location.Incoming, staged.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_removal_waits_for_a_download_in_progress_and_the_download_reads_what_it_started_on()
    {
        await Write("pricing", Notes, "net of tax");

        var hold = await _location.HoldForReadingAsync("pricing", Notes, Ct);

        var change = _location.ChangeAsync("pricing", Notes, Ct);
        Assert.False(change.IsCompleted);

        using (var reader = new StreamReader(hold.Stream, leaveOpen: true))
        {
            Assert.Equal("net of tax", await reader.ReadToEndAsync(Ct));
        }

        await hold.DisposeAsync();

        using (await change.WaitAsync(TimeSpan.FromSeconds(10), Ct))
        {
            _location.Delete("pricing", Notes);
        }

        Assert.False(File.Exists(_location.FilePath("pricing", Notes)));
    }

    [Fact]
    public async Task A_download_arriving_while_a_change_waits_waits_for_the_change()
    {
        await Write("pricing", Notes, "v1");

        var first = await _location.HoldForReadingAsync("pricing", Notes, Ct);
        var change = _location.ChangeAsync("pricing", Notes, Ct);
        var second = _location.HoldForReadingAsync("pricing", Notes, Ct);

        Assert.False(second.IsCompleted);

        await first.DisposeAsync();

        using (await change.WaitAsync(TimeSpan.FromSeconds(10), Ct))
        {
            Assert.False(second.IsCompleted);
        }

        await using var late = await second.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(_location.IsHeld("pricing", Notes));
    }

    [Fact]
    public async Task A_download_that_gives_up_waiting_holds_nothing_afterwards()
    {
        await Write("pricing", Notes, "v1");

        using var cancel = new CancellationTokenSource();
        var change = await _location.ChangeAsync("pricing", Notes, Ct);
        var waiting = _location.HoldForReadingAsync("pricing", Notes, cancel.Token);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        change.Dispose();

        Assert.False(_location.IsHeld("pricing", Notes));
    }

    [Fact]
    public async Task A_prune_waits_for_every_download_anywhere_in_the_location()
    {
        await Write("pricing", Notes, "a");
        await Write("refunds", Notes, "b");

        var one = await _location.HoldForReadingAsync("pricing", Notes, Ct);
        var two = await _location.HoldForReadingAsync("refunds", Notes, Ct);

        var prune = _location.ChangeEverythingAsync(Ct);

        await one.DisposeAsync();
        Assert.False(prune.IsCompleted);

        await two.DisposeAsync();
        (await prune.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Dispose();
    }

    [Fact]
    public async Task On_windows_another_process_cannot_write_a_file_while_it_is_being_downloaded()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "Linux and macOS have no mandatory locks; there the download detects the change by hash instead (SPEC.md §9.5).");

        await Write("pricing", Notes, "v1");

        await using var hold = await _location.HoldForReadingAsync("pricing", Notes, Ct);

        Assert.Throws<IOException>(() =>
            new FileStream(_location.FilePath("pricing", Notes), FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
    }

    [Fact]
    public void A_folder_is_taken_on_disk_whatever_its_case()
    {
        Directory.CreateDirectory(Path.Combine(_root, "pricing"));

        Assert.True(_location.IsTakenOnDisk("Pricing"));
        Assert.False(_location.IsTakenOnDisk("refunds"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../elsewhere")]
    public void A_folder_outside_the_location_is_refused(string folder)
    {
        Assert.Throws<InvalidOperationException>(() => _location.FolderPath(folder));
    }

    [Fact]
    public async Task Preparing_empties_what_a_previous_broker_left_staged()
    {
        await File.WriteAllTextAsync(Path.Combine(_location.Incoming, "left.part"), "half", Ct);

        _location.Prepare();

        Assert.Empty(Directory.EnumerateFiles(_location.Incoming));
    }

    [Fact]
    public void A_full_disk_is_recognised_as_one()
    {
        var location = new FileLocationStore(
            _root + "-full",
            _root + "-full-incoming",
            deleteOnDispose: true,
            createStaging: _ => throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)));

        using (location)
        {
            var full = Assert.Throws<IOException>(() => location.Stage());

            Assert.True(FileLocationStore.IsNoSpace(full));
        }
    }

    [Fact]
    public async Task A_conversations_folder_is_deleted_with_everything_in_it()
    {
        await Write("pricing", Notes, "v1");

        Assert.True(_location.DeleteFolder("pricing"));
        Assert.False(Directory.Exists(_location.FolderPath("pricing")));
    }

    [Fact]
    public async Task A_first_download_waits_out_a_hold_over_everything()
    {
        // A destroy, purge or prune holds the whole location, but a file never read before had no gate for that hold to
        // enter: its first download was let in, streamed a file the hold was about to delete, and kept the folder from
        // going. Found by review, round 20.
        await Write("pricing", Notes, "a");

        var everything = await _location.ChangeEverythingAsync(Ct);
        Task<FileLocationStore.ReadHold> reading;

        try
        {
            reading = _location.HoldForReadingAsync("pricing", Notes, Ct);

            await Task.Delay(50, Ct);
            Assert.False(reading.IsCompleted, "a first download was let in under a hold over everything");
        }
        finally
        {
            everything.Dispose();
        }

        await using var hold = await reading.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task A_change_cancelled_while_it_waits_lets_downloads_in_again()
    {
        // A change waiting for a download holds new downloads back; one given up must stop holding them, though the
        // download it waited for is still going.
        await Write("pricing", Notes, "a");

        await using var reading = await _location.HoldForReadingAsync("pricing", Notes, Ct);

        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var changing = _location.ChangeAsync("pricing", Notes, cancel.Token);
            await Task.Delay(50, Ct);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => changing);
        }

        await using var fresh = await _location.HoldForReadingAsync("pricing", Notes, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_hold_over_everything_cancelled_while_it_waits_lets_downloads_in_again()
    {
        await Write("pricing", Notes, "a");

        await using var first = await _location.HoldForReadingAsync("pricing", Notes, Ct);
        using var cancel = new CancellationTokenSource();
        var everything = _location.ChangeEverythingAsync(cancel.Token);
        await Task.Delay(50, Ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => everything);

        await using var later = await _location.HoldForReadingAsync("pricing", Notes, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_change_to_a_name_never_read_does_not_wait_out_a_hold_over_everything()
    {
        // A change that waited out a destroy waited, with every other agent's, behind any download the destroy waited
        // on. One to a name never read lands before or after the destroy's transaction, so it need not.
        await Write("pricing", Notes, "a");

        using var everything = await _location.ChangeEverythingAsync(Ct);

        using var change = await _location.ChangeAsync("pricing", SharedFileName.Create("other.md"), Ct)
            .WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_commit_never_moves_over_a_file()
    {
        // A placed file is never changed (SPEC.md §9.5): a commit writes only a name nothing holds, so a file put under
        // the name by hand while the placement arrived is refused and kept.
        await File.WriteAllTextAsync(Path.Combine(Directory.CreateDirectory(_location.FolderPath("pricing")).FullName, Notes.Value), "the person's own", Ct);

        await using var staged = _location.Stage();
        await staged.WriteAsync("the agent's"u8.ToArray(), Ct);
        await staged.FinishAsync(Ct);

        var refused = Assert.Throws<Grpc.Core.RpcException>(() => staged.CommitTo("pricing", Notes));
        Assert.Equal(Grpc.Core.StatusCode.Aborted, refused.StatusCode);
        Assert.Equal("the person's own", await File.ReadAllTextAsync(_location.FilePath("pricing", Notes), Ct));
    }

    private async Task Write(string folder, SharedFileName name, string content)
    {
        await using var staged = _location.Stage();
        await staged.WriteAsync(System.Text.Encoding.UTF8.GetBytes(content), Ct);
        await staged.FinishAsync(Ct);
        staged.CommitTo(folder, name);
    }
}
