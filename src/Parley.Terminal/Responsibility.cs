using System.Globalization;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Core;
using Parley.Setup;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// What an agent is responsible for: the one place to read what it has said, change it, or have it say it again.
/// </summary>
/// <remarks>
/// <para>
/// <b>One page where there were two doors.</b> "Say what this agent owns" and "Ask what this agent owns" were the same
/// subject reached by two keys, and somebody choosing between them had to know in advance whether they wanted to write
/// the line or have the agent write it. The page shows what the agent has said and holds the line in a field: changing
/// it and saving is the first, and asking the agent to refresh is the second.
/// </para>
/// <para>
/// <b>It adds no operation.</b> Saving is <c>record_ownership</c> and refreshing is Parley asking again, both of which
/// already existed; what moved is where a person reaches them. Which words were whose is still a comparison and never a
/// flag (<see cref="Ownership.Chosen"/>): the field opens holding an undecided answer of the agent's, so saving it
/// untouched records the agent's words and any change records a person's.
/// </para>
/// <para>
/// <b>Refreshing is refused, with the reason, while an answer is still being decided.</b> Asking over an answer nobody
/// has looked at would lose it, so the page says to save it first. That refusal is the broker's rule and not this
/// screen's; the screen says it before the call rather than after.
/// </para>
/// </remarks>
internal static class Responsibility
{
    /// <summary>The door this page passes through.</summary>
    internal const string Title = "What this agent is responsible for";

    /// <summary>The choice that records the line in the field.</summary>
    internal const string Save = "Save";

    /// <summary>The choice that has the agent say it again.</summary>
    internal const string Refresh = "Ask it to refresh";

    /// <summary>The choice that changes nothing.</summary>
    internal const string Close = "Close";

    /// <summary>Said when saving would change nothing.</summary>
    internal const string Unchanged = "Nothing changed, so nothing was recorded.";

    /// <summary>Said when the agent's last answer is still waiting for a person.</summary>
    internal const string StillWaiting =
        "Its last answer is still waiting for you. Save it, changed or not, and then ask again.";

    /// <summary>Said when the agent asked Parley something back instead of answering.</summary>
    internal const string StillAsked =
        "It is still being asked, or it asked Parley something back and is waiting for a reply. "
        + "Reply from the ownership review, and ask again once that is settled.";

    /// <summary>Opens the page for one agent.</summary>
    /// <param name="app">The application the dialog opens in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="agent">The agent, as the registry last read it.</param>
    /// <param name="registry">The project's registry as last read, for what the other agents claim.</param>
    /// <param name="project">The project whose launch entry carries the line.</param>
    /// <param name="queue">Runs the read and what follows, so the caller can wait for them.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the read has been queued.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        AgentRecord agent,
        IReadOnlyList<AgentRecord> registry,
        string? project,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        queue(async t =>
        {
            var read = await BrokerDirectory.ReadAsync(
                async () => await client.FacilitationAsync(t).ConfigureAwait(false)).ConfigureAwait(false);

            if (!read.Reachable)
            {
                await done($"What it is responsible for could not be read: {Safe.Text(read.Problem!)}")
                    .ConfigureAwait(false);

                return;
            }

            var question = read.Value!.Questions.FirstOrDefault(q => q.Agent == agent.Name);

            // **Refused before the page opens, not after it is edited.** Saving writes a file on the agent's machine, and
            // 0.25.2 records what a rule met after the form closed costs: the words went with it. Asking is not writing a
            // file, so it stays available either way.
            var elsewhere = OwnershipLine.NotHere(agent.Name);

            var page = new ResponsibilityDialog(
                Described(agent, question, Roster.Neighbours(registry, agent.Name), elsewhere),
                Opening(agent, question),
                canSave: elsewhere is null);

            var open = Prompting.Open(app, page);

            page.Done += (_, chosen) =>
            {
                Prompting.Close(app, open);

                switch (chosen.Choice)
                {
                    case ResponsibilityChoice.Save:
                        queue(sending => Saving(
                            app, client, agent, question, chosen.Text, project, sending, queue, done));
                        break;

                    case ResponsibilityChoice.Refresh:
                        Refreshing(app, client, agent, question, queue, done);
                        break;

                    default:
                        _ = done("Nothing was changed.");
                        break;
                }
            };
        });

        return Task.CompletedTask;
    }

    /// <summary>What the field holds when the page opens.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="question">Its latest question, if it was ever asked.</param>
    /// <returns>The line to start from.</returns>
    /// <remarks>
    /// An answer still waiting for a person comes first, because it is the thing the person is here to decide; then
    /// the line already accepted, then the one peers read, then nothing.
    /// </remarks>
    internal static string Opening(AgentRecord agent, OwnershipQuestion? question)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return question is { State: QuestionState.Waiting, Flattened: { Length: > 0 } waiting }
            ? waiting
            : OwnershipStatement.Read(agent.OwnsAccepted) ?? OwnershipStatement.Read(agent.Owns) ?? string.Empty;
    }

    /// <summary>What the person reads above the field.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="question">Its latest question, if it was ever asked.</param>
    /// <param name="neighbours">What the other agents already claim.</param>
    /// <param name="elsewhere">Why the line cannot be written from here, or null when it can.</param>
    /// <returns>The description.</returns>
    /// <remarks>
    /// <b>The agent's words are attributed and marked as its own</b>, with the time, because the record of
    /// <c>proposed</c> against <c>composed</c> is a claim nobody could check from a screen that showed the text alone.
    /// </remarks>
    internal static string Described(
        AgentRecord agent,
        OwnershipQuestion? question,
        IReadOnlyList<(string Name, string Owns)> neighbours,
        string? elsewhere)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(neighbours);

        var name = Safe.Text(agent.Name.Value);

        var peers = OwnershipStatement.Read(agent.Owns) is { } live
            ? $"'{Safe.Text(live)}'"
            : "nothing -- this agent has no line yet";

        var accepted = agent.OwnsPending
            ? $"{Environment.NewLine}Accepted, not yet read by peers: '{Safe.Text(agent.OwnsAccepted!)}' "
              + $"-- {Safe.Text(Provenance.Of(agent.OwnsSource))}."
            : string.Empty;

        var said = question?.Answer is { Length: > 0 } answer
            ? $"""


               {name} said this, at {When(question.AskedUtc)} local time. These are its words, not this screen's:

               {Safe.Lines(answer)}

               Parley: {Safe.Text(Provenance.Of(question))}
               """
            : string.Empty;

        var others = neighbours.Count == 0
            ? string.Empty
            : Environment.NewLine + Environment.NewLine + "What the others already say they own:" + Environment.NewLine
              + string.Join(
                  Environment.NewLine,
                  neighbours.Take(Named).Select(peer => $"  {Safe.Text(peer.Name)}: {Safe.Text(peer.Owns)}"))
              + (neighbours.Count > Named ? $"{Environment.NewLine}  and {neighbours.Count - Named} more." : string.Empty);

        var cannot = elsewhere is null
            ? string.Empty
            : $"{Environment.NewLine}{Environment.NewLine}{Safe.Text(elsewhere)} You can still ask it to refresh.";

        return $"""
                {name}

                Peers read: {peers}{accepted}{said}{others}

                Change the line below and choose {Save}, or choose {Refresh} to have {name} say it again. Saving records
                the words as yours unless you left the agent's own untouched, and takes effect when the agent next starts.{cannot}
                """;
    }

    /// <summary>How many agents are named before the rest are counted.</summary>
    private const int Named = 4;

    /// <summary>When a question was asked, in the reader's own zone.</summary>
    /// <param name="asked">The instant.</param>
    /// <returns>The time, to the minute.</returns>
    private static string When(DateTimeOffset asked) =>
        asked.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Records the line in the field.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="agent">Whose line it is.</param>
    /// <param name="question">Its latest question, if it was ever asked.</param>
    /// <param name="typed">What was in the field.</param>
    /// <param name="project">The project whose launch entry carries the line.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <param name="queue">Runs what follows.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When it has been recorded or refused.</returns>
    private static Task Saving(
        IApplication app,
        BrokerClient client,
        AgentRecord agent,
        OwnershipQuestion? question,
        string typed,
        string? project,
        CancellationToken cancellationToken,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var statement = (typed ?? string.Empty).Trim();

        if (statement.Length == 0)
        {
            return done(Ownership.Nothing);
        }

        var waiting = question is { State: QuestionState.Waiting, Flattened: { Length: > 0 }, Answer: { Length: > 0 } };

        if (!waiting && string.Equals(statement, Opening(agent, question), StringComparison.Ordinal))
        {
            return done(Unchanged);
        }

        // **The agent's own length and the discussion it was agreed in, only for an answer that is still waiting.**
        // Anything else is a person's own line, recorded as composed with no discussion behind it.
        var acceptance = waiting
            ? new OwnershipAcceptance(
                statement,
                Ownership.Chosen(statement, question!.Flattened!),
                question.Answer!.Length,
                question.Conversation)
            : new OwnershipAcceptance(statement, OwnershipSource.Composed, null, null);

        return Ownership.AcceptAsync(
            app, client, agent.Name, agent.Workspace, project, acceptance, cancellationToken, queue, done);
    }

    /// <summary>Has Parley ask the agent again, unless something is already in the way.</summary>
    /// <param name="app">The application the dialog opens in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="agent">Who to ask.</param>
    /// <param name="question">Its latest question, if it was ever asked.</param>
    /// <param name="queue">Runs the ask.</param>
    /// <param name="done">Told what to say when it is over.</param>
    private static void Refreshing(
        IApplication app,
        BrokerClient client,
        AgentRecord agent,
        OwnershipQuestion? question,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        if (question is { State: QuestionState.Waiting })
        {
            _ = done(StillWaiting);

            return;
        }

        if (question is { Live: true })
        {
            _ = done(StillAsked);

            return;
        }

        _ = Ownership.AskAsync(app, client, agent.Name, queue, done);
    }
}

/// <summary>What a person chose on the responsibility page.</summary>
internal enum ResponsibilityChoice
{
    /// <summary>Nothing was changed.</summary>
    Close = 0,

    /// <summary>Record the line in the field.</summary>
    Save = 1,

    /// <summary>Have the agent say it again.</summary>
    Refresh = 2
}

/// <summary>What the responsibility page was closed with.</summary>
/// <param name="Choice">Which button.</param>
/// <param name="Text">What was in the field.</param>
internal sealed record ResponsibilityOutcome(ResponsibilityChoice Choice, string Text);

/// <summary>The page itself: a description, one field, and three ways out.</summary>
internal sealed class ResponsibilityDialog : Dialog
{
    private readonly TextField _field;
    private readonly Label _why;
    private readonly Button _save;

    /// <summary>Creates the page.</summary>
    /// <param name="description">What is shown above the field.</param>
    /// <param name="line">What the field holds on opening.</param>
    /// <param name="canSave">Whether the line can be written from here at all.</param>
    internal ResponsibilityDialog(string description, string line, bool canSave)
    {
        Title = Responsibility.Title;
        Width = Dim.Percent(80);
        Height = Dim.Percent(80);

        var shown = new Plain
        {
            Text = description,
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Fill(5),
        };

        shown.TextFormatter.WordWrap = true;

        // The footer sits against the bottom, so the description keeps everything above it however tall the page is.
        _field = new TextField { X = 1, Y = Pos.AnchorEnd(4), Width = Dim.Fill(2), Text = line };
        _why = new Plain { X = 1, Y = Pos.AnchorEnd(3), Width = Dim.Fill(2), Text = string.Empty };
        _save = new Button { Title = Responsibility.Save, X = 1, Y = Pos.AnchorEnd(2), IsDefault = true, Enabled = canSave };

        var refresh = new Button { Title = Responsibility.Refresh, X = Pos.Right(_save) + 1, Y = Pos.AnchorEnd(2) };
        var close = new Button { Title = Responsibility.Close, X = Pos.Right(refresh) + 1, Y = Pos.AnchorEnd(2) };

        _field.TextChanged += (_, _) => Check(canSave);
        _save.Accepting += (_, args) => { args.Handled = true; Finish(ResponsibilityChoice.Save); };
        refresh.Accepting += (_, args) => { args.Handled = true; Finish(ResponsibilityChoice.Refresh); };
        close.Accepting += (_, args) => { args.Handled = true; Finish(ResponsibilityChoice.Close); };

        Add(shown, _field, _why, _save, refresh, close);
        Check(canSave);
    }

    /// <summary>Raised once, with what the person chose and what the field held.</summary>
    internal event EventHandler<ResponsibilityOutcome>? Done;

    /// <summary>What is in the field now.</summary>
    internal string Typed => _field.Text;

    /// <summary>Why the line in the field cannot be saved, or empty.</summary>
    internal string Problem => _why.Text;

    /// <summary>Whether saving is possible right now.</summary>
    internal bool CanSave => _save.Enabled;

    /// <summary>Ends the page with a choice.</summary>
    /// <param name="choice">Which way out.</param>
    internal void Finish(ResponsibilityChoice choice) =>
        Done?.Invoke(this, new ResponsibilityOutcome(choice, _field.Text));

    /// <inheritdoc/>
    protected override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key == Key.Esc)
        {
            Finish(ResponsibilityChoice.Close);

            return true;
        }

        return base.OnKeyDown(key);
    }

    private void Check(bool canSave)
    {
        var text = _field.Text;
        var problem = string.Empty;

        if (text.Trim().Length > 0 && !OwnershipStatement.IsValid(text, out var wrong))
        {
            problem = wrong ?? string.Empty;
        }

        _why.Text = problem;
        _save.Enabled = canSave && problem.Length == 0;
    }
}
