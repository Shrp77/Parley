using System.Globalization;
using Grpc.Core;
using Parley.Core;

namespace Parley.Client.Files;

/// <summary>
/// What a problem with the file location is, in words, and how many are waiting (SPEC.md §9.5, §10.2
/// <c>list_file_problems</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>One set of words for the command line, the terminal and the web UI</b>, so the three cannot disagree about what a
/// sweep did. Each surface still makes the text safe to draw in its own way: a path and the broker's detail are text
/// nothing checked when something other than Parley put the entry there, and this does not neutralise them.
/// </para>
/// <para>
/// <b>Unknown is not none.</b> A broker that did not answer has an unknown count, and a zero would tell the person
/// there is nothing to look at. So <see cref="WaitingAsync"/> returns <see langword="null"/>, and a surface shows
/// nothing rather than a count it does not have.
/// </para>
/// </remarks>
public static class FileProblemSaid
{
    /// <summary>What a surface tells the person dismissing does, beside the act.</summary>
    public const string DismissingDeletesNothing =
        "Dismissing a problem only takes it off this list. It deletes nothing on disk.";

    /// <summary>How many problems are waiting for the person, or <see langword="null"/> when the broker could not say.</summary>
    /// <param name="broker">The project's operator client.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The count, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Read on every refresh of every surface, so a broker that refuses the call (an older one) or has gone away costs
    /// the count and nothing else.
    /// </remarks>
    public static async Task<int?> WaitingAsync(BrokerClient broker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);

        try
        {
            return (await broker.ListFileProblemsAsync(includeDismissed: false, cancellationToken).ConfigureAwait(false)).Waiting;
        }
        catch (RpcException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // A kind this build does not know: the count is not one this surface can vouch for.
            return null;
        }
    }

    /// <summary>A count, in words, for a status line, a banner or a row.</summary>
    /// <param name="waiting">The count, or <see langword="null"/> when unknown.</param>
    /// <returns>An empty string when there is nothing to say, otherwise a short phrase.</returns>
    public static string Says(int? waiting) =>
        waiting is > 0 and var n
            ? string.Create(CultureInfo.InvariantCulture, $"{n} file problem{(n == 1 ? string.Empty : "s")} waiting")
            : string.Empty;

    /// <summary>What happened, as the person reads it.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The words.</returns>
    public static string Kind(FileProblemKind kind) => kind switch
    {
        FileProblemKind.Quarantined => "Changed on disk, moved to quarantine",
        FileProblemKind.Locked => "Could not be moved or deleted",
        _ => kind.ToString(),
    };

    /// <summary>Where the conversation is said, for a problem whose entry belongs to none.</summary>
    /// <param name="problem">The problem.</param>
    /// <returns>The conversation, or words saying there is none.</returns>
    public static string Conversation(FileProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        return problem.ConversationId ?? "no conversation";
    }

    /// <summary>Everything a surface says about one problem, one fact to a line.</summary>
    /// <param name="problem">The problem.</param>
    /// <returns>The lines, not yet safe to draw.</returns>
    /// <remarks>
    /// The whole hashes, not the short ones: a person comparing a quarantined file with a copy an agent fetched is
    /// comparing every character.
    /// </remarks>
    public static IReadOnlyList<string> Lines(FileProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var lines = new List<string>
        {
            $"{Kind(problem.Kind)}: {problem.Path}",
            $"Conversation: {Conversation(problem)}",
            problem.Detail,
        };

        if (problem.PlacedSha256 is { } placed)
        {
            lines.Add("sha256 placed: " + placed.Value);
        }

        if (problem.FoundSha256 is { } found)
        {
            lines.Add("sha256 found:  " + found.Value);
        }

        if (problem.MovedTo is { } moved)
        {
            lines.Add("Moved to: " + moved + " (in the quarantine directory)");
        }

        if (problem.DismissedUtc is not null)
        {
            lines.Add("Dismissed.");
        }

        return lines;
    }

    /// <summary>Where the quarantine directory is.</summary>
    /// <param name="quarantine">The directory, on the broker's machine.</param>
    /// <returns>The sentence, not yet safe to draw.</returns>
    /// <remarks>
    /// Said on every listing because the directory is the person's (§9.5): Parley moves files into it and never deletes
    /// anything there, so the person is the one who empties it.
    /// </remarks>
    public static string Quarantine(FileLocation quarantine)
    {
        ArgumentNullException.ThrowIfNull(quarantine);

        return $"Quarantined files are kept in {quarantine.Path} on {quarantine.Machine}. Parley never deletes anything there.";
    }
}
