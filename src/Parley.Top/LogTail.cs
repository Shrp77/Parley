using Parley.Core;
using Parley.Setup;

namespace Parley.Top;

/// <summary>
/// The broker's log, followed rather than owned.
/// </summary>
/// <remarks>
/// <para>
/// The one thing on this surface read from disk instead of over the transport, and a convenience
/// view: it can be dropped without touching anything else here.
/// </para>
/// <para>
/// <b>Opened to share, and that is not tidiness.</b> Research R4 measured it in this repository —
/// on the manifest rather than the log, but the mechanism is the same. A reader holding a file
/// with the default share mode does not merely risk a stale read: it <i>stops the writer</i>.
/// <c>File.ReadAllText</c> opens with <c>FileShare.Read</c>, and a rename onto a file held without
/// <c>FileShare.Delete</c> is refused outright — 287 of 300 attempts. A tail is a reader that is
/// open continuously, which is the worst version of that: if the broker ever rotates or replaces
/// its log, a tail holding it the wrong way would block the rotation rather than follow it.
/// </para>
/// </remarks>
internal static class LogTail
{
    /// <summary>How the broker names the file it writes, beside its store.</summary>
    /// <remarks>
    /// The same name <c>AutostartPlan</c> passes to <c>--log</c>. Derived from the project's store
    /// path rather than configured separately, because two places naming one file is how they
    /// come to name different ones.
    /// </remarks>
    internal const string FileName = "parley-broker.log";

    /// <summary>Where this project's broker writes its log, if it writes one where it usually does.</summary>
    /// <param name="project">The project.</param>
    /// <returns>The path, or <see langword="null"/> when the project names no store.</returns>
    /// <remarks>
    /// A guess, and honestly one: a broker started by hand with <c>--log</c> elsewhere writes
    /// elsewhere, and nothing in the manifest records where. So a missing file is reported as
    /// "not where it usually is" rather than as "the broker is not logging".
    /// </remarks>
    internal static string? For(ProjectSettings project) => For(project, null);

    /// <summary>Where this project's broker writes its log, given what the supervisor runs.</summary>
    /// <param name="project">The project.</param>
    /// <param name="installed">The autostart entry, or null for none or unknown.</param>
    /// <returns>The path, or <see langword="null"/> when nothing derivable names one.</returns>
    /// <remarks>
    /// An entry serving the manifest writes one log for every project, beside the manifest, and
    /// not beside any store. Derived from the entry when there is one, because the guess beside
    /// the store is exactly wrong on the machine that switched entries: the old log is still
    /// there beside the old store, and following it shows a broker that stopped writing hours
    /// ago while the live one writes somewhere else. Found by reading the spec's assumption
    /// ("beside the store, as the installer already reports") after the installer stopped
    /// reporting that.
    /// </remarks>
    internal static string? For(ProjectSettings project, InstalledEntry? installed)
    {
        if (installed is { ServesManifest: true, Manifest: { Length: > 0 } manifest })
        {
            try
            {
                var beside = Path.GetDirectoryName(Path.GetFullPath(manifest));

                return beside is null ? null : Path.Combine(beside, FileName);
            }
            catch (Exception failure) when (failure is ArgumentException
                                                    or NotSupportedException
                                                    or PathTooLongException)
            {
                return null;
            }
        }

        if (project is null || string.IsNullOrWhiteSpace(project.Store))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(project.Store));

            return directory is null ? null : Path.Combine(directory, FileName);
        }
        catch (Exception failure) when (failure is ArgumentException
                                                or NotSupportedException
                                                or PathTooLongException)
        {
            // A manifest is a file somebody edits, so a store path that is not a path is a state
            // this can be in. Returning "no log here" makes the log view say so and leaves every
            // other screen working; throwing would take the surface down over its one convenience
            // view, which is the opposite of how the rest of this contains a broken project.
            return null;
        }
    }

    /// <summary>Prints new lines as they are written.</summary>
    /// <param name="write">Where each line goes.</param>
    /// <param name="path">The file to follow.</param>
    /// <param name="filter">Only lines containing this, or everything when blank.</param>
    /// <param name="cancellationToken">Stops following.</param>
    /// <returns>When following stops.</returns>
    /// <remarks>
    /// <para>
    /// Starts at the end rather than replaying the file. Somebody opening this wants to know what
    /// the broker is doing now; a log that has been running for a week would otherwise scroll past
    /// for a minute before reaching the present.
    /// </para>
    /// <para>
    /// <b>And reopens when the file it is holding stops being the file at that path.</b> The share
    /// flags below always allowed the broker to truncate or rotate underneath this — what they did
    /// not do is make the reader notice. Parked past the end of a file that has just been emptied,
    /// every read returns nothing for ever, and the screen goes on saying <i>Following …</i> while
    /// showing nothing: **exactly what a quiet broker looks like**, which is the one thing a log
    /// view must never be confusable with. Found by truncating the file under a running follow.
    /// </para>
    /// </remarks>
    internal static async Task FollowAsync(
        Action<string> write,
        string path,
        string filter,
        CancellationToken cancellationToken)
    {
        try
        {
            // The first open is the only one that skips what is already there. A reopen is a new
            // file, and everything in it is news.
            var fromEnd = true;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadAsync(write, path, filter, fromEnd, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return;
                }

                fromEnd = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped watching, which is the ordinary way out of here.
        }
        catch (IOException failure)
        {
            write($"The log could not be followed: {failure.Message}");
        }
    }

    /// <summary>Reads one opening of the file, until it is replaced or watching stops.</summary>
    /// <param name="write">Where each line goes.</param>
    /// <param name="path">The file to follow.</param>
    /// <param name="filter">Only lines containing this, or everything when blank.</param>
    /// <param name="fromEnd">Whether to skip what the file already holds.</param>
    /// <param name="cancellationToken">Stops following.</param>
    /// <returns><see langword="true"/> when the file should be opened again.</returns>
    private static async Task<bool> ReadAsync(
        Action<string> write,
        string path,
        string filter,
        bool fromEnd,
        CancellationToken cancellationToken)
    {
        // ReadWrite | Delete. Anything less makes this reader a lock on the broker's own log:
        // it could not append, and could not rotate the file out from under us either.
        using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        using var reader = new StreamReader(file);

        if (fromEnd)
        {
            file.Seek(0, SeekOrigin.End);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                if (Replaced(path, file.Position))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken)
                    .ConfigureAwait(false);

                continue;
            }

            if (filter.Length > 0
                && !line.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Escaped, because a log line is not this program's text. It carries whatever an
            // agent's payload put in it, and a bracket read as markup would let a message
            // choose how the operator's own log is drawn.
            write(Safe.Text(line));
        }

        return false;
    }

    /// <summary>Whether the file at this path is no longer the one being read.</summary>
    /// <param name="path">Where the log is.</param>
    /// <param name="position">How far into the open file the reader has got.</param>
    /// <returns><see langword="true"/> when it should be opened again from the start.</returns>
    /// <remarks>
    /// <para>
    /// Shorter than the reader has already read is the signal, which catches both ways a log stops
    /// being itself: emptied in place, and deleted and written again. Asked only when a read came
    /// back with nothing, so a busy log pays nothing for it.
    /// </para>
    /// <para>
    /// It is a heuristic and the limit is worth stating: a rotation that renames the file away and
    /// grows its replacement past this position inside one 200ms tick is not caught. Comparing
    /// creation times would not fix that either — Windows preserves the creation time of a file
    /// recreated at the same path within a few seconds, so the more precise-looking test is the
    /// one that quietly fails on the platform this is most used on.
    /// </para>
    /// </remarks>
    private static bool Replaced(string path, long position)
    {
        var now = new FileInfo(path);

        // Gone rather than replaced. Nothing to reopen yet, so the open handle stays -- a broker
        // mid-rotation has not stopped writing, and reopening a path with no file at it would
        // turn a momentary gap into an error on screen.
        return now.Exists && now.Length < position;
    }
}
