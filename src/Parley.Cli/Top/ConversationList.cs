using Parley.Client;
using Parley.Core;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// The conversations in a project, and the way into one.
/// </summary>
/// <remarks>
/// A conversation identifier is opaque and chosen by an agent (§5.0), so the same one may exist in
/// two projects. Nothing here is reachable without the project it belongs to — opening one without
/// saying which is a coin toss the reader would never know had been made.
/// </remarks>
internal static class ConversationList
{
    /// <summary>Lists the conversations and opens whichever is chosen.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="directory">The clients.</param>
    /// <param name="project">The project to read.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>When the person has gone back.</returns>
    internal static async Task ShowAsync(
        IAnsiConsole console,
        BrokerDirectory directory,
        ProjectSettings project,
        CancellationToken cancellationToken)
    {
        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .ConversationsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!read.Reachable)
        {
            console.MarkupLine($"[red]{Safe.Text(read.Problem!)}[/]");

            return;
        }

        var conversations = read.Value?.Items ?? [];

        if (conversations.Count == 0)
        {
            console.MarkupLine(
                "[yellow]No conversation has happened in this project yet.[/]");

            return;
        }

        console.Write(Table(conversations));

        var labels = conversations.ToDictionary(Label, c => c.Id, StringComparer.Ordinal);

        var choice = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Which conversation?")
                .PageSize(15)
                .AddChoices([.. labels.Keys, Back]));

        if (choice == Back)
        {
            return;
        }

        await TranscriptView.ShowAsync(
            console, directory, project, labels[choice], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The conversations as a table.</summary>
    /// <param name="conversations">What the broker said.</param>
    /// <returns>The table.</returns>
    private static Table Table(IReadOnlyList<ConversationSummary> conversations)
    {
        var table = new Table().Border(TableBorder.Rounded);

        table.AddColumn("conversation");
        table.AddColumn("participants");
        table.AddColumn("messages");
        table.AddColumn("state");
        table.AddColumn("last activity");

        foreach (var conversation in conversations)
        {
            table.AddRow(
                Safe.Text(conversation.Id),
                Safe.Text(string.Join(", ", conversation.Participants.Select(p => p.Value))),
                conversation.Messages.ToString(System.Globalization.CultureInfo.InvariantCulture),
                conversation.HasOpenExchange ? "[yellow]open[/]" : "[dim]settled[/]",
                $"[dim]{conversation.LastActivityUtc.ToLocalTime():yyyy-MM-dd HH:mm}[/]");
        }

        return table;
    }

    /// <summary>How one conversation reads in the chooser.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <returns>Its identifier and who was in it.</returns>
    /// <remarks>
    /// The identifier alone is often a hash somebody's adapter generated, which distinguishes two
    /// rows without telling a reader which one they meant. The participants are what a person
    /// actually remembers a conversation by.
    /// </remarks>
    private static string Label(ConversationSummary conversation) =>
        Safe.Text(
            $"{conversation.Id}  "
            + $"({string.Join(", ", conversation.Participants.Select(p => p.Value))})");

    private const string Back = "Back";
}
