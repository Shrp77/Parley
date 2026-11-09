using Parley.Core;

namespace Parley.Setup;

/// <summary>
/// Retiring, reinstating and purging an agent, as every surface does it: what is said before, the name typed back,
/// the broker's operation, then this project's files.
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, because the terminal and the web console must do exactly the same thing.</b> They had
/// drifted: the terminal withdrew the launch entry and the catalogue line and asked for the name, and the web console
/// did neither, so the same click left a repository still announcing itself on one surface and not the other. Each
/// surface now keeps only its own way of asking; what is asked, what is checked and what is done live here.
/// </para>
/// <para>
/// <b>The files are withdrawn after the broker has answered, never before.</b> A refused or failed operation leaves
/// the repository exactly as it was, so somebody who sees "not registered" has changed nothing, and an agent whose
/// launch configuration went while its registry row stayed would be reachable, unstartable and invisible.
/// </para>
/// <para>
/// <b>Everything here acts on one project.</b> Each project is its own store (SPEC.md §4.1), so the same repository
/// in another project is another agent, and neither its row nor its launch entry for that project is touched.
/// </para>
/// </remarks>
public static class AgentWithdrawal
{
    /// <summary>What retiring does, said before it is chosen.</summary>
    public const string Retiring =
        "Retiring takes it off this project's roster and routes and keeps its record and its history. "
        + "Work it still holds stays open and is flagged as held by a retired agent, so you can close those "
        + "conversations or reinstate it.";

    /// <summary>What purging does, in SPEC.md §14's words for whose parts go, and that the files go with it.</summary>
    public const string Purging =
        "Purging deletes every conversation it took part in, including the halves belonging to "
        + "participants who did nothing wrong, and each one's folder of files, with anything copied into it by hand. "
        + "It cannot be undone.";

    /// <summary>What retiring and purging do to files, said before either is chosen.</summary>
    public const string TheFiles =
        "Either then takes its entry for this project out of its repository's .mcp.json, and its line out of this "
        + "machine's catalogue, so it no longer starts for this project. Reinstating does not put them back.";

    /// <summary>What is said when a seat is reached by some route that should not have offered it.</summary>
    public const string NotAnAgent = "That is a seat, not an agent. A person leaves a seat; they are not retired.";

    /// <summary>What is said after reinstating, because reinstating writes no file back.</summary>
    public const string Reinstated = "No file was recreated: add it again for it to be startable.";

    /// <summary>What is said when nothing was done.</summary>
    public const string Kept = "Kept.";

    /// <summary>What is said when the name typed back was not the agent's.</summary>
    public const string WrongName = "That is not this agent's name. Nothing changed.";

    /// <summary>What asking for the name says.</summary>
    public const string TypeTheName = "Type the agent's name to go ahead:";

    /// <summary>The acceptance a person gives to purge an agent that still owes work.</summary>
    public const string AcceptOwed = "I accept that, and want to purge it anyway";

    /// <summary>That the act reaches one project and no other.</summary>
    /// <param name="project">The project in view, or null when none is named.</param>
    /// <returns>One sentence.</returns>
    public static string ThisProjectOnly(string? project) =>
        project is { Length: > 0 }
            ? $"This happens in {project} only: if the same repository is an agent in other projects, it stays there."
            : "This happens in this project only: if the same repository is an agent in other projects, it stays there.";

    /// <summary>Why a typed name would not be accepted, or null when it would.</summary>
    /// <param name="agent">The agent's whole name.</param>
    /// <param name="typed">What was typed.</param>
    /// <returns>The reason, or null.</returns>
    /// <remarks>
    /// Ordinal and exact, as destroying a project is: trimming would accept a name with a space on the end, which is a
    /// different string from the one somebody was asked to reproduce, and ignoring case would accept an agent that does
    /// not exist.
    /// </remarks>
    public static string? TypedNameProblem(string agent, string? typed) =>
        string.Equals(typed, agent, StringComparison.Ordinal) ? null : WrongName;

    /// <summary>What the agent owes right now, as one line.</summary>
    /// <param name="owed">Every exchange it is waited on in.</param>
    /// <returns>"nothing right now", or how many and who waits.</returns>
    public static string Owing(IReadOnlyList<WaitingEdge> owed)
    {
        ArgumentNullException.ThrowIfNull(owed);

        return owed.Count == 0
            ? "nothing right now"
            : $"{owed.Count} open exchange(s): " + string.Join(", ", owed.Select(edge => $"{edge.Waiter.Value} is waiting"));
    }

    /// <summary>What purging an agent that still owes work does to whoever is waiting.</summary>
    /// <param name="agent">The agent's whole name.</param>
    /// <param name="owed">Every exchange it is waited on in; not empty.</param>
    /// <returns>The consequence, then what each choice does.</returns>
    /// <remarks>
    /// Retirement's outcome is the kinder one, those exchanges staying open for a person to close or for the agent to
    /// be reinstated, so somebody choosing the permanent one should be choosing between them knowingly.
    /// </remarks>
    public static string OwedConsequence(string agent, IReadOnlyList<WaitingEdge> owed)
    {
        ArgumentNullException.ThrowIfNull(owed);

        return $"{agent} owes a Result in {owed.Count} open exchange(s). "
            + string.Join(
                " ",
                owed.Select(edge => $"{edge.Waiter.Value} is waiting, and will find the exchange gone rather than still open."));
    }

    /// <summary>Retires, then withdraws this project's files.</summary>
    /// <param name="broker">This project's broker.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="workspace">Its repository, as the registry holds it, or null when none is recorded.</param>
    /// <param name="project">The project, whose launch entry is the one taken.</param>
    /// <param name="catalogueFile">This machine's catalogue, or null when there is none.</param>
    /// <param name="cancellationToken">Stops the call and the file work.</param>
    /// <returns>What happened, as sentences.</returns>
    public static async Task<string> RetireAsync(
        IParleyBrokerClient broker,
        AgentName agent,
        string? workspace,
        string? project,
        string? catalogueFile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(broker);

        var changed = await broker.RetireAsync(agent, cancellationToken).ConfigureAwait(false);

        var said = new List<string> { changed ? $"Retired '{agent.Value}'." : $"'{agent.Value}' was not registered." };

        if (changed)
        {
            await WithdrawAsync(agent, workspace, project, catalogueFile, said, cancellationToken).ConfigureAwait(false);
        }

        return Sentences(said);
    }

    /// <summary>Purges, then withdraws this project's files.</summary>
    /// <param name="broker">This project's broker.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="workspace">Its repository, as the registry holds it, or null when none is recorded.</param>
    /// <param name="project">The project, whose launch entry is the one taken.</param>
    /// <param name="catalogueFile">This machine's catalogue, or null when there is none.</param>
    /// <param name="cancellationToken">Stops the call and the file work.</param>
    /// <returns>What happened, the broker's toll per table included, as sentences.</returns>
    public static async Task<string> PurgeAsync(
        IParleyBrokerClient broker,
        AgentName agent,
        string? workspace,
        string? project,
        string? catalogueFile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(broker);

        var outcome = await broker.PurgeAsync(agent, cancellationToken).ConfigureAwait(false);
        var gone = outcome.Verdict == PurgeVerdict.Gone;

        var said = new List<string> { gone ? $"Purged '{agent.Value}'." : $"'{agent.Value}' was not registered." };

        // The broker's own account, per table, rather than a summary of it. An agent that traded nothing leaves an
        // empty toll, which is a fact and not a failure.
        said.AddRange(outcome.Destroyed.Select(toll => $"{toll.Table}: {toll.Rows}"));

        if (gone)
        {
            await WithdrawAsync(agent, workspace, project, catalogueFile, said, cancellationToken).ConfigureAwait(false);
        }

        return Sentences(said);
    }

    /// <summary>Reinstates. Writes no file back.</summary>
    /// <param name="broker">This project's broker.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <returns>What happened, as sentences.</returns>
    public static async Task<string> ReinstateAsync(
        IParleyBrokerClient broker, AgentName agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(broker);

        var changed = await broker.ReinstateAsync(agent, cancellationToken).ConfigureAwait(false);

        return (changed ? $"Reinstated '{agent.Value}'. " : $"'{agent.Value}' was not retired. ") + Reinstated;
    }

    /// <summary>Joins what was said into sentences, each ending in a stop.</summary>
    /// <param name="said">The parts.</param>
    /// <returns>One line.</returns>
    public static string Sentences(IEnumerable<string> said)
    {
        ArgumentNullException.ThrowIfNull(said);

        return string.Join(
            ' ',
            said.Select(part => (part ?? string.Empty).ReplaceLineEndings(" ").Trim())
                .Where(part => part.Length > 0)
                .Select(part => part[^1] is '.' or '!' or '?' or ':' ? part : part + "."));
    }

    /// <summary>Takes this project's launch entry and the catalogue line away, and lists what went.</summary>
    private static async Task WithdrawAsync(
        AgentName agent,
        string? workspace,
        string? project,
        string? catalogueFile,
        List<string> said,
        CancellationToken cancellationToken)
    {
        if (workspace is not { Length: > 0 } || workspace == "—")
        {
            said.Add("No workspace is recorded for it, so there was no launch configuration to remove.");

            return;
        }

        try
        {
            var outcome = await Enrolment
                .WithdrawAsync(workspace, agent.Value, project, catalogueFile, cancellationToken)
                .ConfigureAwait(false);

            said.AddRange(outcome.Notes);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Said rather than thrown, and after the operation rather than instead of it. The broker has already
            // changed; a surface that reported a failure here would leave somebody believing it had not happened.
            said.Add($"The files could not be withdrawn: {failure.Message}");
        }
    }
}
