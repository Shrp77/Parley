using System.Globalization;
using System.Text;
using Parley.Client.Files;
using Parley.Client.Rendering;
using Parley.Core;

namespace Parley.Cli;

/// <summary>
/// <c>parley files list|remove|prune|open|folder</c>: the person's operations on a project's file location
/// (SPEC.md §9.5, §10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>What deletes describes first and asks.</b> <c>remove</c> lists each file, its size and who placed it, and asks
/// for <c>yes</c>; <c>prune</c> says how many files and how much space and asks for the project's name typed back.
/// <c>--yes</c> and <c>--yes-remove-every-file</c> skip the question for a script, and are spelled so that nobody
/// types the second by accident.
/// </para>
/// <para>
/// <b>Every name printed is somebody else's text</b>, so each line is flattened: a character that would start a new
/// line or move the cursor is replaced before it reaches the terminal, and a file name cannot forge a second row of
/// this report.
/// </para>
/// <para>
/// Opening launches on this machine only when the broker says the location is here; anywhere else it says which
/// machine and which path, and launches nothing (<see cref="LocalLauncher"/>).
/// </para>
/// </remarks>
/// <param name="broker">The project's broker.</param>
/// <param name="output">Where the report goes.</param>
/// <param name="error">Where refusals and problems go.</param>
/// <param name="input">Where a confirmation is read from.</param>
/// <param name="launcher">Opens files and folders on this machine.</param>
/// <param name="projectName">The project's name, which a prune is confirmed with, or <see langword="null"/>.</param>
public sealed class FilesCommand(
    IParleyBrokerClient broker,
    TextWriter output,
    TextWriter error,
    TextReader input,
    LocalLauncher launcher,
    string? projectName)
{
    private const int Usage = 64;

    private const int NothingDone = 1;

    /// <summary>What <c>parley files</c> with no subcommand, or a wrong one, says.</summary>
    internal const string Help =
        "Usage: parley files list [--conversation <id>] [--full]\n" +
        "       parley files remove <name>... [--conversation <id>] [--yes]\n" +
        "       parley files prune [--yes-remove-every-file]\n" +
        "       parley files open <name>... [--conversation <id>]\n" +
        "       parley files folder [--conversation <id>]";

    /// <summary>The options this command reads itself, which the tool's refusal of unknown options must let through.</summary>
    /// <remarks>
    /// The tool refuses any argument still starting with a hyphen before a command runs, and every other command's
    /// options are taken centrally for that reason. These are not, because no other command has them and this one
    /// already refuses an option it does not know; so they are named here, once, for that refusal to read.
    /// </remarks>
    internal static readonly IReadOnlySet<string> OwnOptions =
        new HashSet<string>(["--conversation", "-c", "--full", "--yes", "--yes-remove-every-file"], StringComparer.Ordinal);

    private readonly IParleyBrokerClient _broker = broker ?? throw new ArgumentNullException(nameof(broker));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly TextWriter _error = error ?? throw new ArgumentNullException(nameof(error));
    private readonly TextReader _input = input ?? throw new ArgumentNullException(nameof(input));
    private readonly LocalLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));

    /// <summary>Runs one subcommand.</summary>
    /// <param name="arguments">Everything after <c>files</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The exit code.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
        {
            await _error.WriteLineAsync(Help).ConfigureAwait(false);
            return Usage;
        }

        var parsed = Parse(arguments.Skip(1).ToList());

        if (parsed is null)
        {
            await _error.WriteLineAsync(Help).ConfigureAwait(false);
            return Usage;
        }

        // An option a subcommand has no use for is refused rather than ignored: `list --yes` succeeding tells somebody a
        // flag did something, and `prune --yes` asking anyway tells them nothing about why.
        if (Takes.TryGetValue(arguments[0], out var takes) && parsed.Given.FirstOrDefault(o => !takes.Contains(o)) is { } stray)
        {
            await _error.WriteLineAsync($"'{stray}' is not an option `parley files {arguments[0]}` takes.").ConfigureAwait(false);
            await _error.WriteLineAsync(Help).ConfigureAwait(false);
            return Usage;
        }

        return arguments[0] switch
        {
            "list" when parsed.Names.Count == 0 => await ListAsync(parsed, cancellationToken).ConfigureAwait(false),
            "remove" when parsed.Names.Count > 0 => await RemoveAsync(parsed, cancellationToken).ConfigureAwait(false),
            "prune" when parsed.Names.Count == 0 && parsed.Conversation is null =>
                await PruneAsync(parsed, cancellationToken).ConfigureAwait(false),
            "open" when parsed.Names.Count > 0 => await OpenAsync(parsed, cancellationToken).ConfigureAwait(false),
            "folder" when parsed.Names.Count == 0 => await FolderAsync(parsed, cancellationToken).ConfigureAwait(false),
            _ => await UsageAsync().ConfigureAwait(false),
        };
    }

    /// <summary>A size in the units a person reads.</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The size, with its unit.</returns>
    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KiB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB")
    };

    /// <summary>Makes somebody else's text safe to print as part of one line.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with every control and formatting character replaced.</returns>
    internal static string Flat(string text)
    {
        var flat = new StringBuilder(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);

            flat.Append(category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                ? "?"
                : rune.ToString());
        }

        return flat.ToString();
    }

    private async Task<int> ListAsync(Options options, CancellationToken cancellationToken)
    {
        var files = new List<SharedFile>();
        SharedFileListing? last = null;
        string? next = null;

        // Every page, because this is one report for a person rather than a call a model reads; the broker still
        // bounds each call it answers.
        do
        {
            last = await _broker.ListSharedFilesAsync(options.Conversation, FileLimits.MaximumPageSize, next, cancellationToken)
                .ConfigureAwait(false);

            files.AddRange(last.Page.Files);
            next = last.Page.NextPageToken;
        }
        while (next is not null);

        if (files.Count == 0)
        {
            await _output.WriteLineAsync("The file location is empty.").ConfigureAwait(false);
            await NotListedAsync(last).ConfigureAwait(false);
            await PlacementsAsync(last.Placements).ConfigureAwait(false);
            return 0;
        }

        foreach (var conversation in files.GroupBy(f => f.ConversationId, StringComparer.Ordinal))
        {
            var bytes = conversation.Sum(f => f.SizeBytes);

            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{Flat(conversation.Key)}  ({conversation.Count()} file(s), {Size(bytes)})")).ConfigureAwait(false);

            foreach (var file in conversation)
            {
                await _output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {Flat(file.Name.Value),-40} {Size(file.SizeBytes),10}  " +
                    $"{(options.Full ? file.Sha256.Value : file.Sha256.Short)}  " +
                    $"{(file.PlacedBy is { } placer ? placer.Value : "the person"),-24} " +
                    $"{LocalTime.Write(file.ChangedUtc)}")).ConfigureAwait(false);
            }
        }

        await _output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{files.Count} file(s), {Size(files.Sum(f => f.SizeBytes))} in all.")).ConfigureAwait(false);

        // Local, as every other time this tool and the screens show a person, and said once rather than per row.
        await _output.WriteLineAsync(LocalTime.Note()).ConfigureAwait(false);

        await NotListedAsync(last).ConfigureAwait(false);
        await PlacementsAsync(last.Placements).ConfigureAwait(false);

        return 0;
    }

    private async Task NotListedAsync(SharedFileListing listing)
    {
        if (listing.NotListedSentence is { } sentence)
        {
            await _output.WriteLineAsync(sentence).ConfigureAwait(false);
        }
    }

    private async Task PlacementsAsync(IReadOnlyList<FilePlacementCount> placements)
    {
        if (placements.Count == 0)
        {
            return;
        }

        await _output.WriteLineAsync("Placements:").ConfigureAwait(false);

        foreach (var count in placements)
        {
            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"  {Flat(count.Agent),-24} {count.Placements} placement(s), {Size(count.Bytes)}")).ConfigureAwait(false);
        }
    }

    private async Task<int> RemoveAsync(Options options, CancellationToken cancellationToken)
    {
        if (options.Names.Count > FileLimits.NamesPerRemoval)
        {
            await _error.WriteLineAsync(
                $"At most {FileLimits.NamesPerRemoval} files can be removed at once. Nothing was removed.").ConfigureAwait(false);
            return Usage;
        }

        var references = options.Names.Select(n => new FileRef(options.Conversation, n)).ToList();

        if (!options.Yes)
        {
            await _output.WriteLineAsync(
                "These will be removed, whichever agent placed them, and every agent that fetched one is told it is " +
                "gone. It cannot be undone:").ConfigureAwait(false);

            foreach (var reference in references)
            {
                var located = await _broker.DescribeFileLocationAsync(reference.ConversationId, reference.Name, cancellationToken)
                    .ConfigureAwait(false);

                await _output.WriteLineAsync(located.Location is { } location
                    ? $"  {Flat(reference.Name)}  {Flat(location.Path)}"
                    : $"  {Flat(reference.Name)}  ({Flat(located.Refusal?.Detail ?? "not in the location")})").ConfigureAwait(false);
            }

            await _output.WriteAsync("Type yes to remove them: ").ConfigureAwait(false);

            if (!string.Equals((await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim(), "yes", StringComparison.Ordinal))
            {
                await _output.WriteLineAsync("Nothing was removed.").ConfigureAwait(false);
                return NothingDone;
            }
        }

        var outcome = await _broker.RemoveFilesAsync(references, cancellationToken).ConfigureAwait(false);

        foreach (var removed in outcome.Removed)
        {
            await _output.WriteLineAsync($"Removed {Flat(removed.ConversationId ?? string.Empty)}/{Flat(removed.Name)}.").ConfigureAwait(false);
        }

        foreach (var refused in outcome.Refused)
        {
            await _error.WriteLineAsync(
                $"Not removed: {Flat(refused.File.Name)}. {Flat(refused.Detail ?? refused.Rejection.ToString())}").ConfigureAwait(false);
        }

        await _output.WriteLineAsync($"Freed {Size(outcome.BytesFreed)}.").ConfigureAwait(false);

        return outcome.Refused.Count == 0 ? 0 : NothingDone;
    }

    private async Task<int> PruneAsync(Options options, CancellationToken cancellationToken)
    {
        var listing = await _broker.ListSharedFilesAsync(null, 1, null, cancellationToken).ConfigureAwait(false);

        if (listing.Page.TotalFiles == 0)
        {
            await _output.WriteLineAsync("The file location is empty. There is nothing to prune.").ConfigureAwait(false);
            return 0;
        }

        if (!options.YesRemoveEveryFile)
        {
            var confirmation = projectName ?? "remove every file";

            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Every file in the location will be removed: {listing.Page.TotalFiles} file(s), " +
                $"{Size(listing.Page.TotalBytes)}. Conversations and messages are untouched, and every agent that " +
                $"fetched a file is told it is gone. It cannot be undone.")).ConfigureAwait(false);

            await _output.WriteAsync(projectName is null
                ? $"Type '{confirmation}' to confirm: "
                : "Type the project's name to confirm: ").ConfigureAwait(false);

            if (!string.Equals((await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim(), confirmation, StringComparison.Ordinal))
            {
                await _output.WriteLineAsync("Nothing was removed.").ConfigureAwait(false);
                return NothingDone;
            }
        }

        var pruned = await _broker.PruneFilesAsync(cancellationToken).ConfigureAwait(false);

        await _output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Removed {pruned.FilesRemoved} file(s), freeing {Size(pruned.BytesFreed)}.")).ConfigureAwait(false);

        return 0;
    }

    private async Task<int> OpenAsync(Options options, CancellationToken cancellationToken)
    {
        var code = 0;

        foreach (var name in options.Names)
        {
            var located = await _broker.DescribeFileLocationAsync(options.Conversation, name, cancellationToken)
                .ConfigureAwait(false);

            if (located.Location is not { } location)
            {
                await _error.WriteLineAsync($"{Flat(name)}: {Flat(located.Refusal?.Detail ?? "not in the location.")}").ConfigureAwait(false);
                code = NothingDone;
                continue;
            }

            var launched = _launcher.OpenFile(location);

            await _output.WriteLineAsync(Flat(launched.Said)).ConfigureAwait(false);
        }

        return code;
    }

    private async Task<int> FolderAsync(Options options, CancellationToken cancellationToken)
    {
        var located = await _broker.DescribeFileLocationAsync(options.Conversation, null, cancellationToken)
            .ConfigureAwait(false);

        if (located.Location is not { } location)
        {
            await _error.WriteLineAsync(Flat(located.Refusal?.Detail ?? "That conversation has no files.")).ConfigureAwait(false);
            return NothingDone;
        }

        await _output.WriteLineAsync(Flat(_launcher.OpenFolder(location).Said)).ConfigureAwait(false);

        return 0;
    }

    private async Task<int> UsageAsync()
    {
        await _error.WriteLineAsync(Help).ConfigureAwait(false);
        return Usage;
    }

    /// <summary>The options each subcommand reads; any other it is given is refused.</summary>
    private static readonly Dictionary<string, IReadOnlySet<string>> Takes = new(StringComparer.Ordinal)
    {
        ["list"] = new HashSet<string>(["--conversation", "--full"], StringComparer.Ordinal),
        ["remove"] = new HashSet<string>(["--conversation", "--yes"], StringComparer.Ordinal),
        ["prune"] = new HashSet<string>(["--yes-remove-every-file"], StringComparer.Ordinal),
        ["open"] = new HashSet<string>(["--conversation"], StringComparer.Ordinal),
        ["folder"] = new HashSet<string>(["--conversation"], StringComparer.Ordinal),
    };

    private static Options? Parse(IReadOnlyList<string> arguments)
    {
        var names = new List<string>();
        var given = new List<string>();
        string? conversation = null;
        bool full = false, yes = false, everyFile = false;

        for (var i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "--conversation" or "-c" when i + 1 < arguments.Count:
                    conversation = arguments[++i];
                    given.Add("--conversation");
                    break;
                case "--full":
                    full = true;
                    given.Add("--full");
                    break;
                case "--yes":
                    yes = true;
                    given.Add("--yes");
                    break;
                case "--yes-remove-every-file":
                    everyFile = true;
                    given.Add("--yes-remove-every-file");
                    break;
                case var flag when flag.StartsWith("--", StringComparison.Ordinal):
                    return null;
                default:
                    names.Add(arguments[i]);
                    break;
            }
        }

        return new Options(names, conversation, full, yes, everyFile, given);
    }

    private sealed record Options(
        IReadOnlyList<string> Names,
        string? Conversation,
        bool Full,
        bool Yes,
        bool YesRemoveEveryFile,
        IReadOnlyList<string> Given);
}
