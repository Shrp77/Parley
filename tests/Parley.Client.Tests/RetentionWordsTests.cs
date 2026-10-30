using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>That a prune says under which policy it ran, and never promises the age cap spares uncollected mail.</summary>
public sealed class RetentionWordsTests
{
    [Fact]
    public void The_policy_applied_is_said_in_days_or_hours()
    {
        Assert.Equal(
            "Policy applied: a conversation quiet for 3 days is pruned, and nothing but open work is kept past 30 days.",
            RetentionWords.Applied(RetentionPolicy.Default));
        Assert.Contains("quiet for 1 hour is", RetentionWords.Applied(new RetentionPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(1))), StringComparison.Ordinal);
    }

    [Fact]
    public void What_a_prune_keeps_says_the_age_cap_reaches_uncollected_mail()
    {
        // SPEC.md §9.4: only open work outlives the age cap. The dialogs once said nothing not yet collected is removed.
        Assert.Contains("until they pass the age cap", RetentionWords.WhatItKeeps, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing not yet collected", RetentionWords.WhatItKeeps, StringComparison.Ordinal);
    }
}
