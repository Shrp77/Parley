using Parley.Client;
using Parley.Client.Files;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Placing a file from this machine in a conversation, as the person (SPEC.md §10.2 <c>place_file</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The only way the person hands an agent a file.</b> A file copied into a conversation's folder by hand is never
/// listed and the next sweep deletes it (§9.5), so a surface that only opened the folder would leave the person no way in.
/// </para>
/// <para>
/// <b>From the project's files, the conversation is chosen first</b>; from a conversation's files it is that one. Only
/// conversations the project lists are offered, so a placement never names one that does not exist.
/// </para>
/// <para>
/// <b>The file is read on this machine and streamed to the broker</b>, wherever the broker runs. The path is checked
/// as it is typed; what the broker refuses — a name holding other content, an extension other than <c>.md</c> or
/// <c>.txt</c> — comes back in the broker's own words, made safe to draw.
/// </para>
/// </remarks>
internal static class FilesPlacing
{
    /// <summary>What the dialogs are called.</summary>
    internal const string Title = "Place a file";

    /// <summary>What the choice that leads here is called on the files list.</summary>
    internal const string Choice = "Place a file…";

    /// <summary>What the form asks for first.</summary>
    private const string PathLabel = "The file's path on this machine (.md or .txt):";

    /// <summary>What the form asks for second.</summary>
    private const string NameLabel = "The name to place it under (empty: the file's own name):";

    private const string Back = "Back";

    private const string NoFile = "There is no file at that path.";

    /// <summary>Asks which conversation when none is given, then for the file.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for the project.</param>
    /// <param name="conversation">The conversation, or <see langword="null"/> to choose one.</param>
    /// <param name="queue">Runs the reads and the placement, so the caller can wait for them.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the first dialog is open, or its read queued.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        string? conversation,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        if (conversation is not null)
        {
            Ask(app, client, conversation, queue, done);

            return Task.CompletedTask;
        }

        queue(async cancellationToken =>
        {
            var read = await BrokerDirectory.ReadAsync(
                () => EveryConversationAsync(client, cancellationToken)).ConfigureAwait(false);

            if (!read.Reachable)
            {
                await done(read.Problem!).ConfigureAwait(false);
                return;
            }

            Choose(app, client, read.Value!, queue, done);
        });

        return Task.CompletedTask;
    }

    /// <summary>One conversation's row in the choice: its id and who is in it.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <returns>The row, safe to draw.</returns>
    /// <remarks>Indented, for the reason <see cref="Files.Row"/> gives.</remarks>
    private static string Row(ConversationSummary conversation) =>
        "  " + Safe.Text(conversation.Participants.Count == 0
            ? conversation.Id
            : $"{conversation.Id}  {string.Join(", ", conversation.Participants.Select(p => p.Value))}");

    /// <summary>What is wrong with a typed path, or <see langword="null"/>.</summary>
    /// <param name="typed">What was typed.</param>
    /// <returns>The problem.</returns>
    private static string? PathProblem(string typed) =>
        File.Exists(Unquoted(typed)) ? null : NoFile;

    /// <summary>What is wrong with a typed name, or <see langword="null"/>.</summary>
    /// <param name="typed">What was typed; empty takes the file's own name.</param>
    /// <returns>The problem.</returns>
    /// <remarks>
    /// Checked on the spelling the broker would store, so a name the broker only renames is not refused here; the
    /// broker checks again, and its word is the one that counts.
    /// </remarks>
    private static string? NameProblem(string typed) =>
        typed.Length == 0
            ? null
            : SharedFileName.Check(SharedFileName.Safe(typed)) is var problem && problem != SharedFileNameProblem.None
                ? SharedFileName.Describe(problem)
                : null;

    /// <summary>A path without the quotes a file browser's "copy as path" puts round it.</summary>
    /// <param name="typed">What was typed.</param>
    /// <returns>The path.</returns>
    private static string Unquoted(string typed)
    {
        var path = typed.Trim();

        return path.Length >= 2 && path[0] == '"' && path[^1] == '"' ? path[1..^1] : path;
    }

    private static void Choose(
        IApplication app,
        BrokerClient client,
        IReadOnlyList<ConversationSummary> conversations,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        if (conversations.Count == 0)
        {
            _ = done("This project has no conversation to place a file in. A file goes in a conversation that exists.");
            return;
        }

        var rows = conversations.Select(Row).ToList();

        var choosing = Prompting.Choose(
            Title,
            "Which conversation does the file go in? It is placed as yours, and any agent in the project can fetch it.",
            [Back, .. rows]);

        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            var index = chosen is null ? -1 : rows.IndexOf(chosen);

            if (index < 0)
            {
                return;
            }

            Ask(app, client, conversations[index].Id, queue, done);
        };
    }

    private static void Ask(
        IApplication app,
        BrokerClient client,
        string conversation,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var form = new FormDialog(
            Title + " in " + Safe.Text(conversation),
            [
                new FormField(PathLabel, string.Empty, PathProblem),
                new FormField(NameLabel, string.Empty, NameProblem),
            ]);

        var token = Prompting.Open(app, form);

        form.Done += (_, values) =>
        {
            Prompting.Close(app, token);

            if (values is null)
            {
                _ = done("Nothing was placed.");
                return;
            }

            var path = Unquoted(values[0]);
            var name = values[1].Trim().Length > 0 ? values[1].Trim() : Path.GetFileName(path);

            queue(t => PlaceAsync(client, conversation, path, name, t, done));
        };
    }

    private static async Task PlaceAsync(
        BrokerClient client,
        string conversation,
        string path,
        string name,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        Stream content;

        try
        {
            content = File.OpenRead(path);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            await done(Safe.Text($"{path} could not be read: {unreadable.Message} Nothing was placed.")).ConfigureAwait(false);
            return;
        }

        ProjectRead<PersonPlacement> read;

        await using (content.ConfigureAwait(false))
        {
            read = await BrokerDirectory.ReadAsync(
                () => client.PlacePersonsFileAsync(conversation, name, content, cancellationToken)).ConfigureAwait(false);
        }

        await done(!read.Reachable
            ? read.Problem!
            : Safe.Text(PlacementSaid.Of(read.Value!, name, Files.Size))).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ConversationSummary>> EveryConversationAsync(
        BrokerClient client,
        CancellationToken cancellationToken)
    {
        var conversations = new List<ConversationSummary>();
        string? next = null;

        do
        {
            var page = await client.ConversationsAsync(pageToken: next, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            conversations.AddRange(page.Items);
            next = page.NextPageToken;
        }
        while (next is not null);

        return conversations;
    }
}
