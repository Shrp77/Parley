using Parley.Client;
using Parley.Core;
using Parley.Setup;
using Terminal.Gui.App;

namespace Parley.Terminal;

/// <summary>
/// Withdrawing an agent from a project: retiring it, which comes back, or purging it, which does
/// not.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one file on this surface that may call the operator's destructive operations</b>, and
/// <c>SurfaceTests</c> holds them here. That is what makes "behind a description" a property of the
/// code rather than a habit: a second path to <c>PurgeAsync</c> fails the build.
/// </para>
/// <para>
/// The rule they are admitted under is the one destroying a project was admitted under, and it is
/// the owner's: what the surface rule asks for is a description first and a deliberate act, and a
/// typed name is a deliberate act. So the order is fixed — describe, choose with keeping first,
/// accept the consequence when there is one, type the name, then the operation, then the files.
/// </para>
/// <para>
/// <b>The files are withdrawn after the broker has answered, never before.</b> A refused or failed
/// operation leaves the repository exactly as it was, so somebody who sees "not registered" has
/// changed nothing — and an agent whose launch configuration went while its registry row stayed
/// would be reachable, unstartable, and invisible to the surface that broke it.
/// </para>
/// <para>
/// <c>ForceConclude</c>, <c>Instruct</c> and <c>Prune</c> are still not here. Admitting one is a
/// decision to argue in <c>contracts/operations.md</c> first, not a call to add.
/// </para>
/// </remarks>
internal static class Withdrawing
{
    /// <summary>What the first dialog is called.</summary>
    internal const string Title = "Remove this agent";

    /// <summary>Keeping it, which is what Enter does.</summary>
    internal const string Keep = "Keep it";

    /// <summary>Retiring it, which can be undone.</summary>
    internal const string Retire = "Retire it (withdrawn from every roster; its record stays)";

    /// <summary>Purging it, which cannot.</summary>
    internal const string Purge = "Purge it (deletes its record and its peers' halves; permanent)";

    /// <summary>What is offered, in the order it is offered.</summary>
    internal static readonly IReadOnlyList<string> Choices = [Keep, Retire, Purge];

    /// <summary>What the dialog reinstating an agent is called.</summary>
    internal const string ReinstateTitle = "Reinstate this agent";

    /// <summary>What is said when nothing was done.</summary>
    internal const string Kept = "Kept.";

    /// <summary>What is said when the name typed back was not the agent's.</summary>
    internal const string WrongName = "That is not this agent's name. Nothing changed.";

    /// <summary>What is said when a seat is reached by some route that should not have offered it.</summary>
    internal const string NotAnAgent =
        "That is a seat, not an agent. A person leaves a seat; they are not retired.";

    /// <summary>What is said after reinstating.</summary>
    internal const string Reinstated =
        "No file was recreated: add it again for it to be startable.";

    /// <summary>What purging does, in SPEC.md §14's own words rather than a paraphrase.</summary>
    internal const string Purging =
        "Purging deletes every conversation it took part in, including the halves belonging to "
        + "peers who did nothing wrong, and cannot be undone.";

    /// <summary>What retiring does.</summary>
    internal const string Retiring =
        "Retiring withdraws it from every roster and every route and keeps its record and its "
        + "history. Work it still holds stays open and is flagged as held by a retired agent, "
        + "so you can close those conversations or reinstate it.";

    /// <summary>The agent, as the first dialog describes it.</summary>
    /// <param name="agent">What the registry says, or null when only the catalogue knows it.</param>
    /// <param name="row">The row as the region drew it.</param>
    /// <param name="owed">Every exchange this agent is waited on in.</param>
    /// <returns>What it is, and what it owes right now.</returns>
    /// <remarks>
    /// Both reads exist already: the registry for role, workspace, adapter, status and presence,
    /// and the waiting graph for what it owes. The debt is the part a person cannot get anywhere
    /// else on this screen, and it is what makes the difference between the two choices concrete.
    /// </remarks>
    internal static string Describe(
        AgentRecord? agent, AgentRow row, IReadOnlyList<WaitingEdge> owed)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(owed);

        // The row's values are already neutralised where they enter it; the adapter is read here
        // and is the agent's own string, so it is neutralised here.
        var said = $"{row.Name}{Environment.NewLine}"
            + $"  state      {row.State}{Environment.NewLine}"
            + $"  workspace  {row.Workspace}{Environment.NewLine}"
            + $"  owns       {row.Owns}{Environment.NewLine}"
            + $"  adapter    {Safe.Text(agent?.Adapter ?? "not registered")}{Environment.NewLine}";

        return owed.Count == 0
            ? said + "  owes       nothing right now"
            : said
              + $"  owes       {owed.Count} open exchange(s): "
              + string.Join(", ", owed.Select(edge => $"{Safe.Text(edge.Waiter.Value)} is waiting"));
    }

    /// <summary>Why a typed name would not be accepted, or null when it would.</summary>
    /// <param name="row">The agent being withdrawn.</param>
    /// <param name="typed">What is in the field.</param>
    /// <returns>The reason, or null.</returns>
    /// <remarks>
    /// Ordinal and exact, for the reason destroying a project is: trimming would accept a name with
    /// a space on the end, which is a different string from the one somebody was asked to
    /// reproduce, and case-insensitivity would accept an agent that does not exist.
    /// </remarks>
    internal static string? TypedNameProblem(AgentRow row, string typed)
    {
        ArgumentNullException.ThrowIfNull(row);

        return string.Equals(typed, row.Name, StringComparison.Ordinal) ? null : WrongName;
    }

    /// <summary>
    /// Runs the whole withdrawal, dialog by dialog.
    /// </summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="row">The agent as the region drew it.</param>
    /// <param name="agent">What the registry says about it, or null.</param>
    /// <param name="owed">Every exchange it is waited on in.</param>
    /// <param name="project">The project being withdrawn from, or null when none is named.</param>
    /// <param name="catalogueFile">The project's catalogue, or null when it names none.</param>
    /// <param name="queue">Runs the operation, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the first dialog is open.</returns>
    internal static Task OpenAsync(
        IApplication app,
        BrokerClient client,
        AgentRow row,
        AgentRecord? agent,
        IReadOnlyList<WaitingEdge> owed,
        string? project,
        string? catalogueFile,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(owed);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(done);

        if (row.Seat)
        {
            // Reached by some route that should not have offered it. Refused rather than trusted
            // to be unreachable: research R7 keeps every seat off this action, whoever holds it.
            _ = done(NotAnAgent);

            return Task.CompletedTask;
        }

        var choosing = Prompting.Choose(
            Title,
            Describe(agent, row, owed)
            + Environment.NewLine + Environment.NewLine + Retiring
            + Environment.NewLine + Purging,
            Choices);

        var token = Prompting.Open(app, choosing);

        choosing.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            switch (chosen)
            {
                case Retire:
                    Name(app, row, Retire, done, () =>
                        queue(t => RetireAsync(client, row, project, catalogueFile, t, done)));
                    break;

                case Purge when owed.Count > 0:
                    Consequence(app, client, row, owed, project, catalogueFile, queue, done);
                    break;

                case Purge:
                    Name(app, row, Purge, done, () =>
                        queue(t => PurgeAsync(client, row, project, catalogueFile, t, done)));
                    break;

                default:
                    _ = done(Kept);
                    break;
            }
        };

        return Task.CompletedTask;
    }

    /// <summary>Puts a retired seat back, so its holder can take it.</summary>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="name">The seat's name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>When the registry has it again.</returns>
    /// <remarks>
    /// <para>
    /// <b>Here rather than beside the seat, and that placement is the check working.</b>
    /// <c>SurfaceTests</c> allows <c>Reinstate</c> in exactly one file, which must be the one
    /// holding the typed-name prompt — so an operator operation cannot acquire a second, quieter
    /// door somewhere else. Taking a retired seat needs the operation, so the call belongs in this
    /// file with the rest of them.
    /// </para>
    /// <para>
    /// <b>It asks for no typed name, and that is argued rather than assumed.</b> The name is
    /// demanded where an act is destructive and irreversible — retiring, purging, destroying — and
    /// reinstating is the undo of one of those. What the seat path does ask for is a sentence
    /// saying what will happen and a deliberate answer to it, which is the half of the rule that
    /// carries the meaning; a name typed back to restore your own seat would be ceremony.
    /// </para>
    /// </remarks>
    internal static Task ReinstateSeatAsync(
        BrokerClient client, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        return client.ReinstateAsync(AgentName.Create(name), cancellationToken);
    }

    /// <summary>Opens the dialog that reinstates a retired agent.</summary>
    /// <param name="app">The application the dialogs open in.</param>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="row">The agent as the region drew it.</param>
    /// <param name="queue">Runs the operation, so the caller can wait for it.</param>
    /// <param name="done">Told what to say when it is over.</param>
    /// <returns>When the dialog is open.</returns>
    internal static Task OfferReinstate(
        IApplication app,
        BrokerClient client,
        AgentRow row,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(queue);

        Name(app, row, ReinstateTitle, done, () => queue(async t =>
        {
            var changed = await client.ReinstateAsync(AgentName.Create(row.Name), t).ConfigureAwait(false);

            await done(
                (changed
                    ? $"Reinstated '{row.Name}'. "
                    : $"'{row.Name}' was not retired. ")
                + Reinstated).ConfigureAwait(false);
        }));

        return Task.CompletedTask;
    }

    /// <summary>Names the asker and what they will see, and takes an explicit acceptance.</summary>
    /// <remarks>
    /// FR-014's second confirmation, and the reason it exists: retirement's outcome is the kinder
    /// one — those exchanges stay open, flagged as held by a retired agent, for a person to close or
    /// to reinstate it — so somebody
    /// choosing the permanent one should be choosing between them knowingly rather than agreeing to
    /// a sentence about themselves.
    /// </remarks>
    private static void Consequence(
        IApplication app,
        BrokerClient client,
        AgentRow row,
        IReadOnlyList<WaitingEdge> owed,
        string? project,
        string? catalogueFile,
        Action<Func<CancellationToken, Task>> queue,
        Func<string, Task> done)
    {
        var accepting = Prompting.Choose(
            Purge,
            $"{row.Name} owes a Result in {owed.Count} open exchange(s). "
            + string.Join(
                " ",
                owed.Select(edge =>
                    $"{edge.Waiter.Value} is waiting, and will find the exchange gone rather than "
                    + "still open."))
            + Environment.NewLine + Environment.NewLine + Purging
            + Environment.NewLine + Retiring,
            [Keep, "I accept that, and want to purge it anyway"]);

        var token = Prompting.Open(app, accepting);

        accepting.Done += (_, chosen) =>
        {
            Prompting.Close(app, token);

            if (chosen is null || chosen == Keep)
            {
                _ = done(Kept);

                return;
            }

            Name(app, row, Purge, done, () => queue(t => PurgeAsync(client, row, project, catalogueFile, t, done)));
        };

    }

    /// <summary>Asks for the agent's name, and goes ahead only if that is what arrives.</summary>
    private static void Name(
        IApplication app, AgentRow row, string title, Func<string, Task> done, Action go)
    {
        var asking = Prompting.Text(
            title,
            $"Type the agent's name to go ahead:",
            string.Empty,
            typed => TypedNameProblem(row, typed));

        var token = Prompting.Open(app, asking);

        asking.Done += (_, typed) =>
        {
            Prompting.Close(app, token);

            if (typed is null || TypedNameProblem(row, typed) is not null)
            {
                // Anything but the name keeps it, and says so. An empty field, a near miss and
                // Escape are all "no", and the difference between them is not one a person should
                // have to know.
                _ = done(Kept);

                return;
            }

            go();
        };
    }

    /// <summary>Retires, then withdraws the two files.</summary>
    private static async Task RetireAsync(
        BrokerClient client,
        AgentRow row,
        string? project,
        string? catalogueFile,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        var changed = await client
            .RetireAsync(AgentName.Create(row.Name), cancellationToken)
            .ConfigureAwait(false);

        var said = new List<string>
        {
            changed ? $"Retired '{row.Name}'." : $"'{row.Name}' was not registered.",
        };

        if (changed)
        {
            await WithdrawAsync(row, project, catalogueFile, said, cancellationToken).ConfigureAwait(false);
        }

        await done(Reports.Sentences(said)).ConfigureAwait(false);
    }

    /// <summary>Purges, then withdraws the two files.</summary>
    private static async Task PurgeAsync(
        BrokerClient client,
        AgentRow row,
        string? project,
        string? catalogueFile,
        CancellationToken cancellationToken,
        Func<string, Task> done)
    {
        var outcome = await client
            .PurgeAsync(AgentName.Create(row.Name), cancellationToken)
            .ConfigureAwait(false);

        var gone = outcome.Verdict == PurgeVerdict.Gone;

        var said = new List<string>
        {
            gone ? $"Purged '{row.Name}'." : $"'{row.Name}' was not registered.",
        };

        // The broker's own account, per table, rather than a summary of it. An agent that traded
        // nothing leaves an empty toll, which is a fact and not a failure.
        said.AddRange(outcome.Destroyed.Select(toll => $"  {toll.Table}: {toll.Rows}"));

        if (gone)
        {
            await WithdrawAsync(row, project, catalogueFile, said, cancellationToken).ConfigureAwait(false);
        }

        await done(Reports.Sentences(said)).ConfigureAwait(false);
    }

    /// <summary>Takes the launch configuration and the catalogue line away, and lists what went.</summary>
    private static async Task WithdrawAsync(
        AgentRow row,
        string? project,
        string? catalogueFile,
        List<string> said,
        CancellationToken cancellationToken)
    {
        if (row.Workspace is not { Length: > 0 } workspace || workspace == "—")
        {
            said.Add("No workspace is recorded for it, so there was no launch configuration to remove.");

            return;
        }

        try
        {
            var outcome = await Enrolment
                .WithdrawAsync(workspace, row.Name, project, catalogueFile, cancellationToken)
                .ConfigureAwait(false);

            said.AddRange(outcome.Notes);
        }
        catch (Exception failure) when (failure is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException)
        {
            // Said rather than thrown, and after the operation rather than instead of it. The
            // broker has already changed; a surface that reported a failure here would leave
            // somebody believing the retirement had not happened.
            said.Add($"The files could not be withdrawn: {failure.Message}");
        }
    }
}
