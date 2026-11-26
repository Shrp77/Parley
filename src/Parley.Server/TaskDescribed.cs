namespace Parley.Server;

/// <summary>What became of an agent's words for one of its background tasks (<see cref="BrokerRuntime.DescribeBackgroundTask"/>).</summary>
/// <param name="Kept">Whether they were kept; false only when the task id or the name was refused.</param>
/// <param name="Refused">Why, when they were not kept.</param>
/// <param name="NameCut">Whether the name was cut to its bound.</param>
/// <param name="DescriptionCut">Whether the description was cut to its bound.</param>
/// <param name="LinkDropped">Why a link that was given was not kept; empty when none was given, or it was kept.</param>
/// <remarks>
/// Says only what happened to the words. Whether the task is running is turn information, which an agent does not read
/// (SPEC.md §10.1), so no answer here depends on it: words for a task the node has not listed yet are kept like any other.
/// </remarks>
public sealed record TaskDescribed(bool Kept, string Refused, bool NameCut, bool DescriptionCut, string LinkDropped)
{
    /// <summary>Words refused whole.</summary>
    /// <param name="why">Why, in words the agent can act on; never a fact about the task.</param>
    /// <returns>The answer.</returns>
    public static TaskDescribed Refusal(string why) => new(false, why, false, false, string.Empty);
}
