namespace Parley.Core;

/// <summary>How a node tells the broker which processes it started (SPEC.md §11.7).</summary>
/// <remarks>
/// One spelling for the two programs that use it — the node that sets it and the adapter that reads it — because two
/// spellings of one name agree until one of them is edited, and then every agent reads as opened outside Parley.
/// </remarks>
public static class LaunchToken
{
    /// <summary>The environment variable a node puts a process's launch token in.</summary>
    public const string Variable = "PARLEY_LAUNCH_TOKEN";
}
