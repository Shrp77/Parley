namespace Parley.Core;

/// <summary>
/// What Results have said across a deployment: SPEC.md §10.2's <c>results()</c>, for §15.
/// </summary>
/// <param name="Outcomes">How many items each agent reported, by outcome and responder.</param>
/// <param name="Blockers">How many blocked items each agent reported, by kind of blocker.</param>
/// <param name="Sources">Whose Results each agent's Results relied on, and how often.</param>
/// <param name="Closures">How many conversations each agent took part in that closed, and how.</param>
/// <remarks>
/// <para>
/// Counts and names, never text. It is read from records the schema fills on every Result and keeps
/// past retention (§9.3), so it answers a year later what the payloads that carried it no longer can.
/// </para>
/// <para>
/// An operator operation because it reads across every agent at once, which §10.1 withholds from a
/// participant.
/// </para>
/// </remarks>
public sealed record ResultsReport(
    IReadOnlyList<OutcomeCount> Outcomes,
    IReadOnlyList<BlockerCount> Blockers,
    IReadOnlyList<SourceCount> Sources,
    IReadOnlyList<ClosureCount> Closures)
{
    /// <summary>Gets a value indicating whether nothing has been recorded yet.</summary>
    /// <value><see langword="true"/> when every list is empty.</value>
    public bool IsEmpty =>
        (Outcomes?.Count ?? 0) == 0 && (Blockers?.Count ?? 0) == 0 &&
        (Sources?.Count ?? 0) == 0 && (Closures?.Count ?? 0) == 0;
}

/// <summary>Items one agent reported with one outcome, under one responder.</summary>
/// <param name="Agent">The agent that sent the Results — or the broker's attribution for <c>closed</c>.</param>
/// <param name="ResponderModel">What produced them (§11.3).</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Items">How many items.</param>
/// <remarks>
/// Split by responder because §15 reads outcome quality by responder, and excludes
/// <c>parley:broker</c>: it writes only <c>closed</c>, on a person's instruction, and counting those
/// would make every model look worse in proportion to how often work was ended early.
/// </remarks>
public sealed record OutcomeCount(string Agent, string ResponderModel, Outcome Outcome, long Items);

/// <summary>Blocked items one agent reported, of one kind.</summary>
/// <param name="Agent">The agent that was blocked.</param>
/// <param name="Kind">What blocked it.</param>
/// <param name="Items">How many items.</param>
public sealed record BlockerCount(string Agent, BlockerKind Kind, long Items);

/// <summary>How often one agent's Results named another as a source.</summary>
/// <param name="Agent">The agent whose Results named the source.</param>
/// <param name="Source">The agent relied on.</param>
/// <param name="Items">How many items named it.</param>
/// <remarks>
/// Work that keeps landing on the same third agent describes a boundary drawn in the wrong place
/// (§15). Pursuing it was right; the fix belongs in the ownership map.
/// </remarks>
public sealed record SourceCount(string Agent, string Source, long Items);

/// <summary>Closed conversations one agent took part in, by who closed them.</summary>
/// <param name="Agent">The participant.</param>
/// <param name="ByConsensus">How many closed because every participant agreed.</param>
/// <param name="ByPerson">How many the person running the deployment closed.</param>
public sealed record ClosureCount(string Agent, long ByConsensus, long ByPerson);
