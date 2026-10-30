using System.Globalization;
using Parley.Client;
using Parley.Core;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// The ownership review: every agent, the line its peers read, where that line came from, and whatever Parley is
/// waiting on — with the decisions a person can make about each (SPEC.md §10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>What makes Auto Accept safe to leave on.</b> A line accepted automatically is one no person
/// read, and this is where a person reads it, sees that nobody had, and corrects it if it is wrong. So the provenance is
/// on every row, in words rather than a colour, from <see cref="Provenance"/> — the one set of words both surfaces use.
/// </para>
/// <para>
/// <b>Nothing here needs a seat.</b> Asking again and replying are Parley's messages, and accepting, editing, writing and
/// discarding are operator decisions, so a person reviewing ownership holds nothing and speaks as nobody.
/// </para>
/// <para>
/// <b>Accepting writes the launch configuration first</b>, through <see cref="Ownership.AcceptAsync"/>, with its
/// machine rule and its order. A person's acceptance also settles the question Parley was waiting on, which the
/// broker does as it records the line.
/// </para>
/// </remarks>
internal static class OwnershipReview
{
    /// <summary>The door the review passes through.</summary>
    internal const string Title = "Review ownership";

    /// <summary>Accepts a waiting answer, flattened, as the agent's words.</summary>
    internal const string Accept = "Accept its answer as it is";

    /// <summary>Opens a waiting answer for editing before it is accepted.</summary>
    internal const string Edit = "Edit its answer, then accept";

    /// <summary>Writes a line with no answer at all.</summary>
    internal const string Write = "Write its line yourself";

    /// <summary>Discards a waiting answer.</summary>
    internal const string Discard = "Discard its answer";

    /// <summary>Has Parley ask again.</summary>
    internal const string AskAgain = "Have Parley ask it again";

    /// <summary>Replies to an agent that asked Parley something back.</summary>
    internal const string Reply = "Reply to what it asked";

    /// <summary>Leaves everything as it is, and is where every dialog here opens.</summary>
    internal const string Nothing = "Leave it as it is";

    /// <summary>Said when there is nobody to review.</summary>
    internal const string NobodyToReview =
        "There is no agent in this project yet.";

    /// <summary>Opens the review for a project.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="registry">The project's registry as last read.</param>
    /// <param name="project">The project whose launch entry carries each line.</param>
    /// <param name="queue">Runs reads and decisions, so the caller can wait for them.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the read has been queued.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        IReadOnlyList<AgentRecord> registry,
        string project,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        var agents = Reviewable(registry);

        if (agents.Count == 0)
        {
            _ = done(NobodyToReview);

            return Task.CompletedTask;
        }

        queue(async t =>
        {
            var read = await BrokerDirectory.ReadAsync(
                async () => await client.FacilitationAsync(t).ConfigureAwait(false)).ConfigureAwait(false);

            if (!read.Reachable)
            {
                await done($"The review could not be read: {Safe.Text(read.Problem!)}").ConfigureAwait(false);

                return;
            }

            var report = read.Value!;
            var rows = agents.Select(a => Row(a, QuestionOf(report, a.Name))).ToList();

            var choosing = Prompting.Choose(Title, Described(report.AutoAccept, agents.Count), rows);
            var open = Prompting.Open(app, choosing);

            choosing.Done += (_, chosen) =>
            {
                Prompting.Close(app, open);

                var at = chosen is null ? -1 : rows.IndexOf(chosen);

                if (at < 0)
                {
                    return;
                }

                Decide(app, client, agents[at], QuestionOf(report, agents[at].Name), project, queue, done);
            };
        });

        return Task.CompletedTask;
    }

    /// <summary>The agents a review lists: every active one that is neither a seat nor Parley.</summary>
    /// <param name="registry">The registry.</param>
    /// <returns>The agents, in name order.</returns>
    internal static IReadOnlyList<AgentRecord> Reviewable(IReadOnlyList<AgentRecord> registry) =>
        [.. registry
            .Where(r => r.Status == AgentStatus.Active)
            .Where(r => !r.Facilitator)
            .Where(r => !string.Equals(r.Adapter, PersonSeat.Adapter, StringComparison.Ordinal))
            .OrderBy(r => r.Name.Value, StringComparer.Ordinal)];

    /// <summary>One agent's row: its name, where its line came from, and what Parley is waiting on.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="question">Its latest question, if it was ever asked.</param>
    /// <returns>One line, safe to draw.</returns>
    internal static string Row(AgentRecord agent, OwnershipQuestion? question)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return $"{Safe.Text(agent.Name.Value)} -- {Provenance.Of(agent.OwnsSource)}; {Safe.Text(Provenance.Of(question))}";
    }

    /// <summary>What a person reads above the list.</summary>
    /// <param name="autoAccept">The project's switch.</param>
    /// <param name="count">How many agents are listed.</param>
    /// <returns>The description.</returns>
    internal static string Described(bool autoAccept, int count) =>
        $"""
         {count} agent(s). Auto Accept is {(autoAccept ? "on" : "off")} for this project: {(autoAccept
             ? "an agent's answer becomes its line with no person reading it, unless it cannot be one or the agent is on another machine."
             : "answers wait here for a person.")}

         Each row says where the line came from. A line "{Provenance.Automatic}" is one nobody has checked.
         """;

    /// <summary>Everything about one agent's line, for a person deciding.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="question">Its latest question, if it was ever asked.</param>
    /// <param name="editing">Whether the person is already editing the answer, so is not pointed at Edit.</param>
    /// <returns>The description.</returns>
    /// <remarks>
    /// <b>A waiting answer is shown as Accept records it, flattened and shortened, and marked as the agent's</b>: what
    /// the person is deciding is whether a model's sentence becomes what every peer reads, which depends on a reader
    /// telling a model's words from a person's. An answer already decided is shown as what it answered, since nothing
    /// here records it.
    /// </remarks>
    internal static string Detail(AgentRecord agent, OwnershipQuestion? question, bool editing = false)
    {
        ArgumentNullException.ThrowIfNull(agent);

        // Ownership lines have no bound, so both are shortened: what this dialog decides is the answer below.
        var peers = agent.Owns is { Length: > 0 } owns ? $"'{Safe.Text(AgentWords.Shortened(owns))}'" : "no line";
        var accepted = agent.OwnsAccepted is { Length: > 0 } line
            ? $"'{Safe.Text(AgentWords.Shortened(line))}' -- {Provenance.Of(agent.OwnsSource)}"
            : Provenance.Of((OwnershipSource?)null);

        // The answer flattened, never as written: blank lines in it would push words below the bottom of a dialog that
        // cannot scroll, and Accept records them as the person's choice. While it waits, a long one says Edit is where the
        // whole of it can be read; once decided, nothing here records it, so neither is said.
        var waiting = question?.State == QuestionState.Waiting;
        var answer = question?.Flattened is { Length: > 0 } flattened
            ? $"""

               What {Safe.Text(agent.Name.Value)} answered{(waiting ? ", as Accept would record it" : string.Empty)}:

               {Safe.Text(AgentWords.Shortened(flattened))}
               """
              + (flattened.Length > AgentWords.ShortLength
                  ? Environment.NewLine + Environment.NewLine
                    + $"That is the start of {flattened.Length.ToString(CultureInfo.InvariantCulture)} characters."
                    + (waiting && !editing ? $" Choose '{Edit}' to read all of it before it is recorded." : string.Empty)
                  : string.Empty)
            : string.Empty;

        // The answer straight after the name: it is what a choice here records, and the dialog cuts its tail.
        return $"""
                {Safe.Text(agent.Name.Value)}{answer}

                Peers read: {peers}
                Accepted: {accepted}
                Parley: {Safe.Text(Provenance.Of(question))}
                """;
    }

    /// <summary>What a person may do about one agent, given where its question stands.</summary>
    /// <param name="question">Its latest question, if it was ever asked.</param>
    /// <returns>The choices, with doing nothing first.</returns>
    /// <remarks>
    /// Only what would do something is offered. A key named where it does nothing is the fault this surface keeps a rule
    /// about, and an answer with nothing waiting cannot be accepted or discarded.
    /// </remarks>
    internal static IReadOnlyList<string> Choices(OwnershipQuestion? question) => question?.State switch
    {
        QuestionState.Waiting => [Nothing, Accept, Edit, Discard, Write],
        QuestionState.AskedBack => [Nothing, Reply, Write],
        QuestionState.Asked or QuestionState.Answered => [Nothing, Write],
        _ => [Nothing, AskAgain, Write]
    };

    private static void Decide(
        IApplication app,
        BrokerClient client,
        AgentRecord agent,
        OwnershipQuestion? question,
        string project,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var deciding = Prompting.Choose(Title, Detail(agent, question), Choices(question));
        var open = Prompting.Open(app, deciding);

        deciding.Done += (_, chosen) =>
        {
            Prompting.Close(app, open);

            switch (chosen)
            {
                case Accept when question is { Flattened: { } line, Answer: { } said }:
                    queue(t => Ownership.AcceptAsync(
                        app, client, agent.Name, agent.Workspace, project,
                        new OwnershipAcceptance(line, OwnershipSource.Proposed, said.Length, question.Conversation),
                        t, queue, done));
                    break;

                case Edit when question is { Flattened: { } line, Answer: { } said }:
                    Editing(app, client, agent, question, line, said.Length, project, queue, done);
                    break;

                case Write:
                    _ = Ownership.OpenAsync(app, client, agent.Name, agent.Workspace, project, queue, done);
                    break;

                case Discard:
                    queue(async t => await Decided(
                        await BrokerDirectory.ReadAsync(
                            async () => await client.DiscardAnswerAsync(agent.Name, t).ConfigureAwait(false))
                            .ConfigureAwait(false),
                        "Discarded. Nothing was written, and it keeps whatever line it had.",
                        done).ConfigureAwait(false));
                    break;

                case AskAgain:
                    _ = Ownership.AskAsync(app, client, agent.Name, queue, done);
                    break;

                case Reply:
                    Replying(app, client, agent.Name, queue, done);
                    break;

                default:
                    _ = done("Nothing was changed.");
                    break;
            }
        };
    }

    private static void Editing(
        IApplication app,
        BrokerClient client,
        AgentRecord agent,
        OwnershipQuestion question,
        string line,
        int answered,
        string project,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        // Opens holding the agent's own line, so leaving it unchanged is recorded as the agent's words and any change as
        // a person's edit — a comparison, never a flag somebody sets (Ownership.Chosen).
        var typing = Prompting.Text(
            Title,
            Detail(agent, question, editing: true),
            line,
            problem: typed => OwnershipStatement.IsLaunchable(typed, out var wrong) ? null : wrong);

        var open = Prompting.Open(app, typing);

        typing.Done += (_, said) =>
        {
            Prompting.Close(app, open);

            var statement = (said ?? string.Empty).Trim();

            if (statement.Length == 0)
            {
                _ = done(Ownership.Nothing);

                return;
            }

            queue(t => Ownership.AcceptAsync(
                app, client, agent.Name, agent.Workspace, project,
                new OwnershipAcceptance(statement, Ownership.Chosen(statement, line), answered, question.Conversation),
                t, queue, done));
        };
    }

    private static void Replying(
        IApplication app,
        BrokerClient client,
        AgentName agent,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var typing = Prompting.Text(
            Reply,
            $"""
             {Safe.Text(agent.Value)} asked Parley something back instead of answering. What you write here is sent
             through Parley and recorded as a person's words, never as Parley's.
             """);

        var open = Prompting.Open(app, typing);

        typing.Done += (_, said) =>
        {
            Prompting.Close(app, open);

            var words = (said ?? string.Empty).Trim();

            if (words.Length == 0)
            {
                _ = done("Nothing was typed, so nothing was sent.");

                return;
            }

            queue(async t => await Decided(
                await BrokerDirectory.ReadAsync(
                    async () => await client.ReplyToAskedBackAsync(agent, words, t).ConfigureAwait(false))
                    .ConfigureAwait(false),
                "Sent through Parley as your words. Its answer shows here when it comes.",
                done).ConfigureAwait(false));
        };
    }

    private static Task Decided(ProjectRead<FacilitationAction> read, string success, Func<string, Task> done) =>
        done(!read.Reachable
            ? $"Nothing was done: {Safe.Text(read.Problem!)}"
            : read.Value!.Done
                ? success
                : $"Nothing was done: {Safe.Text(read.Value.Problem!)}");

    private static OwnershipQuestion? QuestionOf(FacilitationReport report, AgentName agent) =>
        report.Questions.FirstOrDefault(q => q.Agent == agent);
}
