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

        // Whole days from two days up; a single day, or days and hours, are said in hours.
        Assert.Contains("quiet for 24 hours is", RetentionWords.Applied(new RetentionPolicy(TimeSpan.FromHours(24), TimeSpan.FromHours(48))), StringComparison.Ordinal);
        Assert.Contains("kept past 2 days.", RetentionWords.Applied(new RetentionPolicy(TimeSpan.FromHours(24), TimeSpan.FromHours(48))), StringComparison.Ordinal);
        Assert.Contains("kept past 50 hours.", RetentionWords.Applied(new RetentionPolicy(TimeSpan.FromHours(12), TimeSpan.FromHours(50))), StringComparison.Ordinal);
    }

    [Fact]
    public void What_closing_keeps_names_the_idle_period_and_export()
    {
        // SPEC.md §9.4: closing does not shorten the idle period. The terminal's dialog once said only that nothing is deleted.
        Assert.Contains("quiet for the retention idle period", RetentionWords.WhatClosingKeeps, StringComparison.Ordinal);
        Assert.Contains("export it to keep a copy", RetentionWords.WhatClosingKeeps, StringComparison.Ordinal);

        // Closing ends the floor's hold on open work, so text past the age cap can go before the idle period ends. Not at
        // a promised pass: an old Request stays until the closed notice it carries back is collected.
        Assert.Contains("older than the age cap can go sooner, as early as the next retention pass", RetentionWords.WhatClosingKeeps, StringComparison.Ordinal);
        Assert.DoesNotContain("goes at the next", RetentionWords.WhatClosingKeeps, StringComparison.Ordinal);
    }

    [Fact]
    public void What_a_prune_keeps_says_the_age_cap_reaches_uncollected_mail()
    {
        // SPEC.md §9.4: only open work outlives the age cap. The dialogs once said nothing not yet collected is removed.
        Assert.Contains("until they pass the age cap", RetentionWords.WhatItKeeps, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing not yet collected", RetentionWords.WhatItKeeps, StringComparison.Ordinal);
    }
}
