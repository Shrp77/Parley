using Parley.Core;
using Spectre.Console;

namespace Parley.Cli.Top;

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
    internal static string? For(ProjectSettings project)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.Store))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(project.Store));

        return directory is null ? null : Path.Combine(directory, FileName);
    }

    /// <summary>Follows the log until the person stops watching.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="project">Whose log to follow.</param>
    /// <param name="cancellationToken">Stops following.</param>
    /// <returns>When the person has stopped watching.</returns>
    internal static async Task ShowAsync(
        IAnsiConsole console,
        ProjectSettings project,
        CancellationToken cancellationToken)
    {
        var path = For(project);

        if (path is null || !File.Exists(path))
        {
            console.MarkupLine(
                $"[yellow]No log where this project's broker usually writes one[/] "
                + $"[dim]({ProjectList.Escape(path ?? "nowhere derivable")}).[/] A broker started "
                + "with --log elsewhere writes elsewhere, and the manifest does not record where.");

            return;
        }

        var filter = console.Prompt(
            new TextPrompt<string>("Filter [dim](blank shows everything)[/]:")
                .AllowEmpty());

        console.MarkupLine(
            $"[dim]Following {ProjectList.Escape(path)}"
            + (filter.Length > 0 ? $", showing lines containing '{ProjectList.Escape(filter)}'" : string.Empty)
            + ". Press Enter to stop.[/]");

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var watching = FollowAsync(console, path, filter, stopping.Token);

        // Read a key on a worker so the follow loop keeps drawing. Console.ReadLine blocks, and
        // blocking the loop is what turns "press Enter to stop" into "the log stopped updating".
        await Task.Run(Console.ReadLine, cancellationToken).ConfigureAwait(false);

        await stopping.CancelAsync().ConfigureAwait(false);

        await watching.ConfigureAwait(false);
    }

    /// <summary>Prints new lines as they are written.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="path">The file to follow.</param>
    /// <param name="filter">Only lines containing this, or everything when blank.</param>
    /// <param name="cancellationToken">Stops following.</param>
    /// <returns>When following stops.</returns>
    /// <remarks>
    /// Starts at the end rather than replaying the file. Somebody opening this wants to know what
    /// the broker is doing now; a log that has been running for a week would otherwise scroll past
    /// for a minute before reaching the present.
    /// </remarks>
    internal static async Task FollowAsync(
        IAnsiConsole console,
        string path,
        string filter,
        CancellationToken cancellationToken)
    {
        try
        {
            // ReadWrite | Delete. Anything less makes this reader a lock on the broker's own log:
            // it could not append, and could not rotate the file out from under us either.
            using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(file);

            file.Seek(0, SeekOrigin.End);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line is null)
                {
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
                console.MarkupLine($"[dim]{ProjectList.Escape(line)}[/]");
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped watching, which is the ordinary way out of here.
        }
        catch (IOException failure)
        {
            console.MarkupLine($"[red]The log could not be followed: {ProjectList.Escape(failure.Message)}[/]");
        }
    }
}
