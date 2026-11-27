using System.Globalization;
using Parley.Core;

namespace Parley.Client.Files;

/// <summary>
/// A conversation's placed files as a person reads them: in its transcript by time, and in its Media view (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Placed by the record, never by the words.</b> A file sits in the transcript where its placement time falls among
/// the messages, because the placement record is the only thing that ties it to the conversation. A message that names
/// a file neither moves nor shows it: payloads are inert (§13).
/// </para>
/// <para>
/// One derivation for the web console and the terminal, so the two screens put a file in the same place and list Media in
/// the same order.
/// </para>
/// </remarks>
public static class Placements
{
    /// <summary>The largest image a surface shows inline, in bytes; a larger one waits for the person to ask.</summary>
    /// <remarks>
    /// Bounds what one page view pulls through the broker and into a browser (SPEC.md §9.5). The pixel size cannot be
    /// known without decoding, which Parley does not do; a decompression bomb under this is the browser decoder's to
    /// refuse, and that is recorded as what remains.
    /// </remarks>
    public const long InlineLimitBytes = 20L * 1024 * 1024;

    /// <summary>The largest file a surface copies to this machine to open, in bytes.</summary>
    /// <remarks>A copy is held in memory while it is read, so it is bounded; a larger file is opened on the broker's machine.</remarks>
    public const long CopyLimitBytes = 256L * 1024 * 1024;

    /// <summary>Reads every file a conversation holds, every page of it.</summary>
    /// <param name="broker">The operator's client.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The files, oldest placement first.</returns>
    public static async Task<IReadOnlyList<SharedFile>> EveryAsync(
        IParleyBrokerClient broker, string conversationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        var files = new List<SharedFile>();
        string? next = null;

        do
        {
            var listing = await broker.ListSharedFilesAsync(conversationId, FileLimits.MaximumPageSize, next, cancellationToken)
                .ConfigureAwait(false);

            files.AddRange(listing.Page.Files.Where(f => string.Equals(f.ConversationId, conversationId, StringComparison.Ordinal)));
            next = listing.Page.NextPageToken;
        }
        while (next is not null);

        return InOrder(files);
    }

    /// <summary>Files in the order they were placed, oldest first; one moment's ties by name.</summary>
    /// <param name="files">The files.</param>
    /// <returns>The same files, ordered.</returns>
    public static IReadOnlyList<SharedFile> InOrder(IEnumerable<SharedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        return [.. files.OrderBy(f => f.PlacedUtc).ThenBy(f => f.Name.Key, StringComparer.Ordinal)];
    }

    /// <summary>Takes from the front of a queue every file placed before a moment.</summary>
    /// <param name="waiting">The files not yet drawn, oldest first, as <see cref="InOrder"/> gives them.</param>
    /// <param name="before">The moment: the next message's time.</param>
    /// <returns>The files to draw before that message.</returns>
    /// <remarks>
    /// Message times come from the machines agents run on and placement times from the broker's, so the order across
    /// the two is as good as those clocks, which the transcript already says of its own times.
    /// </remarks>
    public static IReadOnlyList<SharedFile> Before(Queue<SharedFile> waiting, DateTimeOffset before)
    {
        ArgumentNullException.ThrowIfNull(waiting);

        var due = new List<SharedFile>();

        while (waiting.Count > 0 && waiting.Peek().PlacedUtc < before)
        {
            due.Add(waiting.Dequeue());
        }

        return due;
    }

    /// <summary>A conversation's Media: its images and its other files, each newest first.</summary>
    /// <param name="files">The conversation's files.</param>
    /// <returns>The two lists.</returns>
    public static (IReadOnlyList<SharedFile> Images, IReadOnlyList<SharedFile> Others) Media(IEnumerable<SharedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var newest = InOrder(files).Reverse().ToList();

        return ([.. newest.Where(IsImage)], [.. newest.Where(f => !IsImage(f))]);
    }

    /// <summary>Whether a file is an image, by its name.</summary>
    /// <param name="file">The file.</param>
    /// <returns>Whether its name ends in one of the four image extensions.</returns>
    public static bool IsImage(SharedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return ImageSignature.IsImageName(file.Name.Value);
    }

    /// <summary>Whether a file is small enough to show inline without being asked.</summary>
    /// <param name="file">The file.</param>
    /// <returns>Whether it is no larger than <see cref="InlineLimitBytes"/>.</returns>
    public static bool ShownInline(SharedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return file.SizeBytes <= InlineLimitBytes;
    }

    /// <summary>Who placed a file, as a transcript names them.</summary>
    /// <param name="file">The file.</param>
    /// <returns>The agent's name, or "the person".</returns>
    public static string Placer(SharedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return file.PlacedBy?.Value ?? "the person";
    }

    /// <summary>What kind of file it is, in a word.</summary>
    /// <param name="file">The file.</param>
    /// <returns><c>image</c> or <c>text</c>.</returns>
    public static string Kind(SharedFile file) => IsImage(file) ? "image" : "text";

    /// <summary>A size in the units a person reads.</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The size, with its unit.</returns>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KiB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB"),
    };

    /// <summary>One placement as a line of a transcript.</summary>
    /// <param name="file">The file.</param>
    /// <param name="zone">The reader's zone, or <see langword="null"/> for this machine's.</param>
    /// <returns>Who placed what, its kind and size, and when. Not yet made safe to draw.</returns>
    public static string Line(SharedFile file, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Placer(file)} placed {file.Name.Value} ({Kind(file)}, {Size(file.SizeBytes)}) at {Rendering.LocalTime.Clock(file.PlacedUtc, zone)}");
    }
}
