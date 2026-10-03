namespace Parley.Server;

/// <summary>What a node said about a turn that ended (SPEC.md §11.6).</summary>
/// <param name="StartedUtc">When it began, which pairs it with the start the node reported.</param>
/// <param name="Elapsed">How long it ran.</param>
/// <param name="Clean">Whether it completed without an error.</param>
/// <param name="TimedOut">Whether it was cut off for running too long.</param>
/// <param name="ProcessDied">Whether the agent's process died during it.</param>
/// <param name="PermissionDenials">Tool calls it had refused for want of permission.</param>
/// <param name="CostUsd">What the runner said it cost.</param>
/// <param name="ModelTurns">How many model turns went into it.</param>
public sealed record EndedTurn(
    DateTimeOffset StartedUtc,
    TimeSpan Elapsed,
    bool Clean,
    bool TimedOut,
    bool ProcessDied,
    int PermissionDenials,
    double CostUsd,
    int ModelTurns);
