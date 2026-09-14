using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Parley.Core;

namespace Parley.Store;

/// <summary>Which process holds a store, as that process recorded itself.</summary>
/// <param name="ProcessId">Its process identifier.</param>
/// <param name="StartedUtc">
/// When that process started. It is what tells the holder apart from a later process that happens to
/// have been given the same identifier, which an operating system does freely.
/// </param>
/// <param name="Build">The build it said it was, or <see langword="null"/>.</param>
public sealed record StoreOwner(int ProcessId, DateTimeOffset StartedUtc, string? Build);

/// <summary>
/// The exclusive claim one broker holds on its store for as long as it serves it.
/// </summary>
/// <remarks>
/// <para>
/// <b>SPEC.md §4 says the broker is the only process that opens the database, and until this nothing
/// made that so.</b> The owner met the consequence: a scheduled task and a terminal restoring its last
/// session both started <c>parley serve</c> at logon, the second clearing the first's liveness on its
/// way to failing to bind a port, and every agent on the machine read as stranded while the machine ran
/// them. Starting a broker is a thing people arrange more than once without meaning to, so a second
/// start must be refused before it can write — and must say who holds the store, or the person refused
/// goes looking through every process on the machine for it.
/// </para>
/// <para>
/// <b>A held file rather than a row, and the operating system releases it.</b> A lock recorded in the
/// store would need a heartbeat to tell a live holder from a crashed one, and would be written by the
/// very process it is meant to keep out. A file opened with no sharing belongs to its process: a crash
/// releases it with every other handle, so there is no stale lock to clear and no timeout to choose.
/// On Windows that is a sharing refusal; on Linux and macOS .NET takes an exclusive <c>flock</c>, which
/// holds between separate opens even inside one process.
/// </para>
/// <para>
/// <b>Which process holds it is written beside it, and believed only when it checks out.</b> The lock
/// cannot be read by anybody else on Windows, so the holder records its identifier, its start time and
/// its build in <see cref="StoreFiles.Owner"/>. A refused broker reads that and confirms the process is
/// alive and started when it says — a record left by a process that has gone, or one read in the
/// moment before a new holder rewrote it, names somebody who is not there, and naming nobody is better
/// than naming the wrong process.
/// </para>
/// </remarks>
public sealed class StoreLock : IDisposable
{
    /// <summary>How far a recorded start time may be from the live process's before it is not believed.</summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    private readonly FileStream _held;

    private StoreLock(string storePath, FileStream held)
    {
        StorePath = storePath;
        _held = held;
    }

    /// <summary>Gets the store this claim is on.</summary>
    public string StorePath { get; }

    /// <summary>Claims a store, or reports who already has.</summary>
    /// <param name="storePath">The store. Its directory must already exist.</param>
    /// <returns>The claim, held until it is disposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="storePath"/> is null or empty.</exception>
    /// <exception cref="StoreHeldException">Another process holds the store.</exception>
    /// <exception cref="IOException">The lock file could not be created for any other reason.</exception>
    /// <remarks>
    /// A failure to open is read as held only when the lock file exists afterwards. Anything else that
    /// fails an open — a path too long, a directory missing — is left to propagate as itself, because a
    /// broker told its store is "being served by another broker" when it cannot write the directory
    /// would exit reporting a process that does not exist, and send somebody looking for it.
    /// </remarks>
    public static StoreLock Acquire(string storePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(storePath);

        var lockPath = storePath + StoreFiles.Lock;
        FileStream held;

        try
        {
            held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when (exception is not DirectoryNotFoundException && File.Exists(lockPath))
        {
            throw new StoreHeldException(storePath, ReadOwner(storePath), exception);
        }

        var claimed = new StoreLock(storePath, held);

        claimed.Record();

        return claimed;
    }

    /// <summary>Releases the store.</summary>
    /// <remarks>
    /// The lock file is left where it is, deliberately, for the reason <see cref="StoreFiles.Lock"/>
    /// gives: on Linux a lock belongs to an open file rather than a name, and unlinking it on the way out
    /// would let the next two brokers each lock a different file of the same name. The owner record is
    /// left too; the next holder rewrites it, and a reader confirms it before believing it.
    /// </remarks>
    public void Dispose() => _held.Dispose();

    /// <summary>Reads who holds a store, believing the record only if that process is there.</summary>
    /// <param name="storePath">The store.</param>
    /// <returns>The holder, or <see langword="null"/> when there is no record worth believing.</returns>
    internal static StoreOwner? ReadOwner(string storePath)
    {
        string[] lines;

        try
        {
            lines = File.ReadAllLines(storePath + StoreFiles.Owner);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        string? Field(string name) =>
            lines.FirstOrDefault(line => line.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

        if (!int.TryParse(Field("pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
            || !DateTimeOffset.TryParse(
                Field("started"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started))
        {
            return null;
        }

        var build = Field("build") is { Length: > 0 } said ? said : null;

        try
        {
            using var process = Process.GetProcessById(pid);

            var actual = new DateTimeOffset(process.StartTime.ToUniversalTime());

            return (actual - started).Duration() <= StartTolerance
                ? new StoreOwner(pid, started, build)
                : null;
        }
        catch (ArgumentException)
        {
            // No such process: the record outlived its writer.
            return null;
        }
        catch (InvalidOperationException)
        {
            // It exited between being found and being asked when it started.
            return null;
        }
        catch (Win32Exception)
        {
            // Not ours to inspect. Naming nobody is better than naming the wrong process.
            return null;
        }
    }

    /// <summary>Records this process as the holder, for whoever is refused next.</summary>
    /// <remarks>
    /// Best effort. The lock is the claim and this is only its label: a broker that could not write the
    /// label still owns the store, and one that refused to serve over it would be trading a deployment
    /// for a better error message.
    /// </remarks>
    private void Record()
    {
        DateTimeOffset started;

        try
        {
            using var self = Process.GetCurrentProcess();

            started = new DateTimeOffset(self.StartTime.ToUniversalTime());
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (Win32Exception)
        {
            return;
        }

        var record = string.Create(
            CultureInfo.InvariantCulture,
            $"pid={Environment.ProcessId}\nstarted={started:O}\nbuild={Build()}\n");

        try
        {
            File.WriteAllText(StorePath + StoreFiles.Owner, record);
        }
        catch (IOException)
        {
            // The label, not the claim.
        }
        catch (UnauthorizedAccessException)
        {
            // The same.
        }
    }

    /// <summary>This process's build, as a person would recognise it.</summary>
    /// <returns><c>0.25.6 (7bd3348)</c>, or the version alone, or empty.</returns>
    private static string Build()
    {
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrEmpty(version))
        {
            return string.Empty;
        }

        var plus = version.IndexOf('+', StringComparison.Ordinal);

        return plus < 0
            ? version
            : $"{version[..plus]} ({version[(plus + 1)..][..Math.Min(7, version.Length - plus - 1)]})";
    }
}

/// <summary>
/// A store another broker is already serving.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IOException"/> because that is what it is underneath — a file somebody else has open
/// — and a caller catching I/O failures to report them should still catch this. Its own type because
/// the right response is not the same: every other failure to open a store is a fault to report, and
/// this one is the ordinary result of starting a broker twice, which is something to report and leave.
/// </para>
/// </remarks>
public sealed class StoreHeldException : IOException
{
    /// <summary>Creates the exception for a store another process holds.</summary>
    /// <param name="storePath">The store.</param>
    /// <param name="owner">Who holds it, if that could be confirmed.</param>
    /// <param name="innerException">The refusal the operating system gave.</param>
    public StoreHeldException(string storePath, StoreOwner? owner, Exception? innerException)
        : base(Describe(storePath, owner), innerException)
    {
        StorePath = storePath;
        Owner = owner;
    }

    /// <summary>Creates the exception with a default message.</summary>
    public StoreHeldException()
        : base("The store is being served by another Parley broker.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public StoreHeldException(string? message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public StoreHeldException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the store that is held.</summary>
    public string StorePath { get; } = string.Empty;

    /// <summary>Gets who holds it, or <see langword="null"/> when that could not be confirmed.</summary>
    public StoreOwner? Owner { get; }

    private static string Describe(string storePath, StoreOwner? owner)
    {
        if (owner is null)
        {
            return $"'{storePath}' is being served by another Parley broker. A store has one broker (SPEC.md §4).";
        }

        var pid = owner.ProcessId.ToString(CultureInfo.InvariantCulture);
        var started = owner.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var build = owner.Build is { } said ? ", parley " + said : string.Empty;

        return $"'{storePath}' is being served by another Parley broker: process {pid}, started {started}{build}. " +
               "A store has one broker (SPEC.md §4).";
    }
}
