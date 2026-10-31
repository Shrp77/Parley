using System.Diagnostics;
using System.Globalization;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That a store has one broker, and that the second one is told who has it.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §4 says the broker is the only process that opens the database, and a scheduled task and
/// a terminal restoring its session proved nothing enforced it: both started at logon, and the one
/// that failed to bind had already marked the other's live node disconnected. These check the claim
/// itself; <c>SecondBrokerTests</c> checks that the broker takes it before it writes.
/// </para>
/// <para>
/// In one process rather than two, and that is representative here in a way it was not for the
/// migration race <c>ConcurrentLaunchTests</c> records. A file opened with no sharing refuses a second
/// open on Windows whoever makes it, and .NET's exclusive <c>flock</c> on Linux and macOS belongs to the
/// open file rather than the process, so two opens in one process conflict exactly as two processes do.
/// </para>
/// </remarks>
[Integration]
public sealed class StoreLockTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-lock-" + Guid.NewGuid().ToString("N"));

    public StoreLockTests() => Directory.CreateDirectory(_directory);

    private string Store => Path.Combine(_directory, "parley.db");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort, as elsewhere in this suite.
        }
        catch (UnauthorizedAccessException)
        {
            // The same.
        }
    }

    [Fact]
    public void A_second_claim_on_a_held_store_is_refused()
    {
        using var first = StoreLock.Acquire(Store);

        var refused = Assert.Throws<StoreHeldException>(() => StoreLock.Acquire(Store));

        Assert.Equal(Store, refused.StorePath);
    }

    [Fact]
    public void The_refusal_names_the_process_holding_it()
    {
        // Otherwise the question is answered by going through every process on the machine.
        using var first = StoreLock.Acquire(Store);

        var refused = Assert.Throws<StoreHeldException>(() => StoreLock.Acquire(Store));

        Assert.NotNull(refused.Owner);
        Assert.Equal(Environment.ProcessId, refused.Owner.ProcessId);
        Assert.Contains(
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Releasing_it_lets_the_next_broker_take_it()
    {
        // Restarting a broker is stopping one and starting the next, so a release that did not
        // release would refuse every restart over a store nobody is serving.
        StoreLock.Acquire(Store).Dispose();

        using var second = StoreLock.Acquire(Store);

        Assert.Equal(Store, second.StorePath);
    }

    [Fact]
    public void The_lock_file_outlives_its_holder()
    {
        // Deleted on release, a lock on Linux would be two locks: the next two starters each create
        // and lock a different file of the same name, and both believe they are alone.
        StoreLock.Acquire(Store).Dispose();

        Assert.True(File.Exists(Store + StoreFiles.Lock));
    }

    [Fact]
    public void An_owner_record_left_by_a_process_that_has_gone_is_not_believed()
    {
        // A crash releases the lock and leaves the record. Held by something that wrote no record of
        // its own, the refusal must name nobody rather than a process that is not there.
        File.WriteAllText(
            Store + StoreFiles.Owner,
            "pid=2147483000\nstarted=2020-01-01T00:00:00.0000000+00:00\nbuild=0.0.1\n");

        using var holder = new FileStream(
            Store + StoreFiles.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var refused = Assert.Throws<StoreHeldException>(() => StoreLock.Acquire(Store));

        Assert.Null(refused.Owner);
    }

    [Fact]
    public void A_process_identifier_reused_by_somebody_else_is_not_believed()
    {
        // Operating systems hand identifiers out again. A record naming this process's identifier
        // with a start time that is not this process's is somebody else's record.
        using var self = Process.GetCurrentProcess();

        var wrongStart = new DateTimeOffset(self.StartTime.ToUniversalTime()).AddHours(-3);

        File.WriteAllText(
            Store + StoreFiles.Owner,
            string.Create(CultureInfo.InvariantCulture, $"pid={Environment.ProcessId}\nstarted={wrongStart:O}\nbuild=\n"));

        using var holder = new FileStream(
            Store + StoreFiles.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var refused = Assert.Throws<StoreHeldException>(() => StoreLock.Acquire(Store));

        Assert.Null(refused.Owner);
    }

    [Fact]
    public void A_store_whose_directory_is_missing_is_not_reported_as_held()
    {
        // Read as held, a broker would exit saying another broker serves this store -- sending
        // somebody to look for a process that does not exist, when what is missing is a directory.
        var nowhere = Path.Combine(_directory, "missing", "parley.db");

        var failure = Assert.ThrowsAny<IOException>(() => StoreLock.Acquire(nowhere));

        Assert.IsNotType<StoreHeldException>(failure);
    }

    [Fact]
    public void An_owner_record_name_that_is_a_link_is_replaced_never_written_through()
    {
        // Found by review, round 52: the record was written through a link left at its name, overwriting whatever the
        // link reached.
        var outside = Path.Combine(_directory, "persons-notes.txt");
        File.WriteAllText(outside, "the person's own notes");

        try
        {
            File.CreateSymbolicLink(Store + StoreFiles.Owner, outside);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"this machine will not create a symbolic link for a test ({refused.GetType().Name})");
        }

        using (StoreLock.Acquire(Store))
        {
            Assert.Null(new FileInfo(Store + StoreFiles.Owner).LinkTarget);
            Assert.Contains("pid=", File.ReadAllText(Store + StoreFiles.Owner), StringComparison.Ordinal);
        }

        Assert.Equal("the person's own notes", File.ReadAllText(outside));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_directory),
            f => Path.GetFileName(f).StartsWith("parley.db" + StoreFiles.Owner + ".", StringComparison.Ordinal));
    }
}
