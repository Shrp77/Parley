using System.Globalization;
using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>A retention policy and what pruning keeps, in the words both surfaces use.</summary>
/// <remarks>
/// One place, so the terminal and the web console say the same thing about the same numbers (SPEC.md §9.4): a person told
/// what a prune removed is told under which policy, because a deletion under numbers nobody realised were in force is the
/// mistake worth making visible.
/// </remarks>
public static class RetentionWords
{
    /// <summary>What a prune removes and what it keeps, said before the person confirms it.</summary>
    /// <remarks>
    /// The age cap reaches a message nobody collected and the request an uncollected result carries; only text on an
    /// exchange still open outlives it, until a person closes its conversation.
    /// </remarks>
    public const string WhatItKeeps =
        "Nothing on an open exchange is removed. A message not yet collected, and the request a result not yet collected "
        + "carries back, are kept until they pass the age cap, and then removed with the rest.";

    /// <summary>What closing a conversation does to its text, said before the person confirms it.</summary>
    /// <remarks>
    /// Closing does not shorten the idle period (SPEC.md §9.4), and a person expecting to read a conversation back is
    /// told how long they have and how to keep it. Closing ends the floor's hold on open work, so text already past the
    /// age cap can go sooner. "Can", and "as early as": an old Request stays until the closed notice it carries back is
    /// collected (§9.4's carried-Request clause), so no pass is promised.
    /// </remarks>
    public const string WhatClosingKeeps =
        "Nothing is deleted now. Its text is kept until the conversation has been quiet for the retention idle period, "
        + "three days unless the broker is set otherwise, but anything already older than the age cap can go sooner, as "
        + "early as the next retention pass; export it to keep a copy.";

    /// <summary>The policy a prune applied, as one sentence.</summary>
    /// <param name="policy">The policy.</param>
    /// <returns>The sentence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    public static string Applied(RetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return $"Policy applied: a conversation quiet for {Span(policy.IdlePeriod)} is pruned, and nothing but open work "
               + $"is kept past {Span(policy.MaximumAge)}.";
    }

    private static string Span(TimeSpan span) =>
        span.TotalHours >= 48 && span.TotalHours % 24 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalDays:0} days")
            : span.TotalHours == 1
                ? "1 hour"
                : string.Create(CultureInfo.InvariantCulture, $"{span.TotalHours:0.##} hours");
}
