using Parley.Client;
using Parley.Core;
using Parley.Setup;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Recording what a person says an agent owns (SPEC.md §10.2 <c>record_ownership</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this screen writes is the person's own line, and it says so.</b> §17 question 10's
/// amendment permits a line an agent drafted, on the condition that approval is a recorded positive
/// act — so a surface that could not tell a person's words from an agent's would make the record
/// worth nothing. This one records <see cref="OwnershipSource.Composed"/>, because there is no ask
/// here: a line an agent proposed arrives through the conversation it was agreed in, carries that
/// conversation with it, and is recorded as the agent's.
/// </para>
/// <para>
/// <b>It has to say that nothing an asker reads has changed.</b> The registry is filled from the
/// launch environment when the agent attaches (§12.3), so accepting a line is a decision until the
/// agent next starts. A screen reporting only "recorded" would be true about the record and false
/// about the deployment, which is the shape 0.25.2 records paying for elsewhere.
/// </para>
/// </remarks>
internal static class Ownership
{
    /// <summary>The door this operation passes through.</summary>
    internal const string Title = "Say what this agent owns";

    /// <summary>What the field asks for.</summary>
    internal const string TypeIt = "One line, read by every agent that lists the roster:";

    /// <summary>Said when the person gives nothing.</summary>
    internal const string Nothing = "Nothing was typed, so nothing was recorded.";

    /// <summary>Opens the dialogs that record an ownership line.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="agent">Whose line it is.</param>
    /// <param name="workspace">The repository holding it, as the registry records it.</param>
    /// <param name="project">The project whose launch entry carries the line.</param>
    /// <param name="queue">Runs the operation, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the first dialog is open.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        AgentName agent,
        string? workspace,
        string? project,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        // **Refused before the form opens, not after it is filled in.** 0.25.2 records what the other
        // order costs: a rule met after the form closed took somebody's words with it. Nothing about
        // this machine or that repository will change while a person is typing.
        if (OwnershipLine.NotHere(agent) is { } elsewhere)
        {
            _ = done(Safe.Text(elsewhere));

            return Task.CompletedTask;
        }

        var typing = Prompting.Text(Title, Described(agent));
        var typed = Prompting.Open(app, typing);

        typing.Done += (_, said) =>
        {
            Prompting.Close(app, typed);

            var statement = (said ?? string.Empty).Trim();

            if (statement.Length == 0)
            {
                _ = done(Nothing);

                return;
            }

            queue(t => AcceptAsync(client, agent, workspace, project, statement, t, done));
        };

        return Task.CompletedTask;
    }

    /// <summary>What the person reads before they type.</summary>
    /// <param name="agent">Whose line it is.</param>
    /// <returns>The description.</returns>
    /// <remarks>
    /// Names the cost rather than a length, because there is no length any more: the bound came off
    /// at 0.33.0 and what is left is the reason to be brief. A field that said nothing about cost
    /// would leave somebody no reason not to write a paragraph every peer then reads.
    /// </remarks>
    internal static string Described(AgentName agent) =>
        $"""
         {Safe.Text(agent.Value)}

         {TypeIt}

         Every agent in this deployment reads it whenever anybody lists the roster, so it is read far
         more often than it is written, and it costs each of them context every time. Say what this
         agent owns and what it does not -- the exclusion is the half that stops somebody asking the
         wrong agent.

         This records your words as yours. It takes effect when the agent next starts, because the
         line itself comes from its launch configuration.
         """;

    /// <summary>Writes the line where it takes effect, then records that a person accepted it.</summary>
    /// <remarks>
    /// <para>
    /// <b>The file first and the record second, and the order is chosen for which half is
    /// recoverable.</b> A file written with no record leaves the line live and its provenance
    /// unrecorded, which the next acceptance repairs; a record with no file would claim somebody
    /// accepted a line that peers will never read, and nothing repairs a wrong record.
    /// </para>
    /// <para>
    /// So a failed write stops before the broker is told anything — there is nothing to record yet —
    /// and a failed record after a successful write says both halves, because the line really is on
    /// its way to peers and only the provenance is missing.
    /// </para>
    /// </remarks>
    private static async Task AcceptAsync(
        BrokerClient client,
        AgentName agent,
        string? workspace,
        string? project,
        string statement,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        var wrong = await OwnershipLine
            .WriteAsync(workspace, project, agent, statement, cancellationToken)
            .ConfigureAwait(false);

        if (wrong is not null)
        {
            await done($"Not recorded: {Safe.Text(wrong)}").ConfigureAwait(false);

            return;
        }

        var outcome = await BrokerDirectory.ReadAsync(
            async () => await client.RecordOwnershipAsync(
                agent,
                new OwnershipAcceptance(statement, OwnershipSource.Composed, null, null),
                cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!outcome.Reachable)
        {
            await done(
                $"Written to {Safe.Text(OwnershipLine.PathIn(workspace!))}, and it takes effect when "
                + $"the agent next starts. The broker could not be told a person accepted it: "
                + Safe.Text(outcome.Problem!)).ConfigureAwait(false);

            return;
        }

        await done(Said(outcome.Value!)).ConfigureAwait(false);
    }

    /// <summary>What to put on the message line.</summary>
    /// <param name="record">What came back.</param>
    /// <returns>One line, safe to draw.</returns>
    /// <remarks>
    /// Internal so a test can hold the wording without driving a broker, and because
    /// <c>DeadSurfaceTests</c> reports a constant named only in its own file.
    /// </remarks>
    internal static string Said(OwnershipRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!record.Recorded)
        {
            return $"Not recorded: {Safe.Text(record.Problem ?? "the broker gave no reason.")}";
        }

        if (!record.Pending)
        {
            return "Recorded, and it is what peers already read.";
        }

        var live = record.LiveStatement is null
            ? "no line for this agent"
            : $"'{Safe.Text(record.LiveStatement)}'";

        return $"Recorded. Peers read {live} until it next starts, because the line comes from its "
             + "launch configuration.";
    }
}
