using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// Opening a conversation with a peer, and sending into one.
/// </summary>
/// <remarks>
/// <para>
/// The refusals are <see cref="SeatVoice"/>'s, not a copy of them. It checks rule 8.1's intent and
/// consequences and rule 8.5's size cap before anything goes on the wire, and it returns the
/// sentence to show — so this file collects text and prints what it is told. A second
/// implementation of those rules here would be a second opinion about what the protocol requires,
/// and the one that disagreed would be the one that let something through.
/// </para>
/// <para>
/// When the broker refuses anyway, <b>its own words are shown</b>. §7.3 says a refusal returns
/// work rather than a retry instruction, and a surface that replaced the broker's sentence with a
/// generic failure would throw away the only part a person can act on — the shape settled for the
/// adapter in #71, where a refusal stripped of its detail told an agent not to do the very thing
/// the broker had asked for.
/// </para>
/// </remarks>
internal static class Composer
{
    /// <summary>Asks a peer a question, opening a conversation.</summary>
    /// <param name="console">Where to draw, and where the question is typed.</param>
    /// <param name="directory">The clients.</param>
    /// <param name="voice">The person's own agent operations.</param>
    /// <param name="project">The project the peer is in.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>When it has been sent, or abandoned.</returns>
    internal static async Task AskAsync(
        IAnsiConsole console,
        BrokerDirectory directory,
        SeatVoice voice,
        ProjectSettings project,
        CancellationToken cancellationToken)
    {
        if (await PeerAsync(console, directory, project, cancellationToken).ConfigureAwait(false)
            is not { } peer)
        {
            return;
        }

        // Collected before anything is sent, in the order rule 8.1 puts them: what is being
        // decided, then the question, then what turns on the answer. Asking for the question
        // first invites somebody to write it and stop.
        var intent = console.Prompt(new TextPrompt<string>("What are you trying to decide?"));
        var question = console.Prompt(new TextPrompt<string>("The question:"));

        var consequences = console.Prompt(
            new TextPrompt<string>(
                $"What turns on the answer? [dim](at least {SeatVoice.MinimumConsequences} "
                + "characters)[/]"));

        var conversation = console.Prompt(
            new TextPrompt<string>("Conversation name:")
                .DefaultValue(Suggested()));

        var said = await voice
            .AskAsync(peer, intent, question, consequences, conversation, cancellationToken)
            .ConfigureAwait(false);

        console.MarkupLine($"[yellow]{ProjectList.Escape(said)}[/]");
    }

    /// <summary>Tells a peer something, expecting no answer.</summary>
    /// <param name="console">Where to draw, and where the message is typed.</param>
    /// <param name="directory">The clients.</param>
    /// <param name="voice">The person's own agent operations.</param>
    /// <param name="project">The project the peer is in.</param>
    /// <param name="conversationId">The conversation to send into.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>When it has been sent, or abandoned.</returns>
    internal static async Task NotifyAsync(
        IAnsiConsole console,
        BrokerDirectory directory,
        SeatVoice voice,
        ProjectSettings project,
        string? conversationId,
        CancellationToken cancellationToken)
    {
        if (await PeerAsync(console, directory, project, cancellationToken).ConfigureAwait(false)
            is not { } peer)
        {
            return;
        }

        var text = console.Prompt(new TextPrompt<string>("What are you telling them?"));

        var conversation = conversationId is { Length: > 0 } named
            ? named
            : console.Prompt(new TextPrompt<string>("Conversation name:").DefaultValue(Suggested()));

        var said = await voice
            .NotifyAsync(peer, text, conversation, cancellationToken)
            .ConfigureAwait(false);

        console.MarkupLine($"[yellow]{ProjectList.Escape(said)}[/]");
    }

    /// <summary>Chooses a peer from the project's registry.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="directory">The clients.</param>
    /// <param name="project">The project to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The peer, or <see langword="null"/> when there is none or none was chosen.</returns>
    /// <remarks>
    /// Chosen from the registry rather than typed, which is SC-001 again: an agent name is
    /// <c>role@machine</c> and asking somebody to type one is asking them to have read a
    /// database. It also means a peer that is not there can be seen not to be there before the
    /// question is written rather than after.
    /// </remarks>
    private static async Task<AgentName?> PeerAsync(
        IAnsiConsole console,
        BrokerDirectory directory,
        ProjectSettings project,
        CancellationToken cancellationToken)
    {
        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .RegistryAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!read.Reachable)
        {
            console.MarkupLine($"[red]{ProjectList.Escape(read.Problem!)}[/]");

            return null;
        }

        var peers = (read.Value ?? [])
            .Where(a => a.Status == AgentStatus.Active)
            .OrderBy(a => a.Name.Value, StringComparer.Ordinal)
            .ToList();

        if (peers.Count == 0)
        {
            console.MarkupLine("[yellow]There is nobody in this project to talk to.[/]");

            return null;
        }

        var labels = new Dictionary<string, AgentName>(StringComparer.Ordinal);

        foreach (var peer in peers)
        {
            labels[$"{peer.Name.Value}  ({Parley.Client.Rendering.Roster.State(peer)})"] = peer.Name;
        }

        var picked = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Who?")
                .PageSize(15)
                .AddChoices([.. labels.Keys, Back]));

        return picked == Back ? null : labels[picked];
    }

    /// <summary>A conversation name nobody has to invent.</summary>
    /// <returns>Something short and unlikely to collide.</returns>
    /// <remarks>
    /// Offered as a default rather than imposed. §5.0 leaves the identifier to the agent choosing
    /// it, and a person continuing an existing thread needs to be able to type its name.
    /// </remarks>
    private static string Suggested() =>
        $"top-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

    private const string Back = "Back";
}
