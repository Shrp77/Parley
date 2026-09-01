using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the roster is one roster, wherever a page shows it.
/// </summary>
/// <remarks>
/// <para>
/// It was two copies of the same markup, and they had already drifted: the project's roster
/// linked each name to that agent's own page and the conversation's did not, so the same list
/// did different things depending on which page a reader arrived from. Nobody decided that — it
/// happened, and then it was the behaviour.
/// </para>
/// <para>
/// A partial fixes today's drift; this stops tomorrow's. The check is that no page draws a roster
/// of its own, which is greppable and survives pages nobody has written yet — unlike comparing
/// the two that exist now.
/// </para>
/// </remarks>
public sealed class RosterConsistencyTests
{
    [Fact]
    public void Every_roster_comes_from_the_one_partial()
    {
        var pages = Directory.GetFiles(
            RepositoryPath("src/Parley.Web/Pages"), "*.cshtml", SearchOption.AllDirectories);

        Assert.NotEmpty(pages);

        var own = pages
            .Where(p => !string.Equals(
                Path.GetFileName(p), "_Roster.cshtml", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p)
                .Contains("class=\"roster\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            own.Count == 0,
            "a page draws its own roster instead of using the partial, which is how the two "
            + "copies this replaced came to behave differently: " + string.Join(", ", own));
    }

    [Fact]
    public void Both_pages_that_show_one_use_it()
    {
        // The other direction. A partial nothing renders is a partial that stops being kept up,
        // and the check above would still pass with every roster deleted.
        foreach (var page in new[]
        {
            "src/Parley.Web/Pages/Projects/Agents.cshtml",
            "src/Parley.Web/Pages/Conversations/Detail.cshtml"
        })
        {
            Assert.Contains(
                "<partial name=\"_Roster\"",
                File.ReadAllText(RepositoryPath(page)),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_partial_makes_every_name_a_way_into_that_agent()
    {
        // The functional half, and the thing the conversation's copy was missing. A roster whose
        // names are inert is a list; one whose names lead somewhere is navigation.
        var partial = File.ReadAllText(
            RepositoryPath("src/Parley.Web/Pages/Shared/_Roster.cshtml"));

        Assert.Contains("/Projects/Agent?project=", partial, StringComparison.Ordinal);
        Assert.Contains("Palette.SlotFor", partial, StringComparison.Ordinal);
        Assert.Contains("entry.State", partial, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_tick_on_the_timeline_lands_on_a_message()
    {
        // Razor reads `m@message.Id` in an attribute as an email address and emits it literally,
        // so the anchors rendered as the text "m@message.Id" and every tick pointed at nothing.
        // The page still looked right, which is why this is asserted rather than eyeballed.
        var markup = File.ReadAllText(
            RepositoryPath("src/Parley.Web/Pages/Conversations/Detail.cshtml"));

        Assert.Contains("id=\"@($\"m{message.Id}\")\"", markup, StringComparison.Ordinal);
        Assert.Contains("href=\"@($\"#m{message.Id}\")\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_abandoned_tick_differs_by_shape_and_not_only_by_colour()
    {
        // The rail is how you find an abandoned exchange in a long conversation. If the only
        // difference were the tint, a reader who cannot see it would have no rail at all.
        var layout = File.ReadAllText(
            RepositoryPath("src/Parley.Web/Pages/Shared/_Layout.cshtml"));

        var ghosted = layout[layout.IndexOf(".timeline .tick.ghosted", StringComparison.Ordinal)..];

        Assert.Contains("height", ghosted[..200], StringComparison.Ordinal);
        Assert.Contains("border", ghosted[..200], StringComparison.Ordinal);
    }

    [Fact]
    public void Selecting_a_message_can_be_matched_to_its_agent_without_reading_a_colour()
    {
        // The rail groups by hue, which is no grouping at all for a reader who cannot separate
        // the hues. Selecting a message marks the rest from that agent, and every signal that
        // does the marking survives colour blindness: the unmatched are dimmed, the matched are
        // outlined, and a sentence says how many and whose.
        //
        // Matched on the agent's own name rather than its palette slot, so the marking cannot
        // drift from whatever colour it happened to be given.
        var markup = File.ReadAllText(
            RepositoryPath("src/Parley.Web/Pages/Conversations/Detail.cshtml"));

        Assert.Contains("data-agent=\"@message.From.Value\"", markup, StringComparison.Ordinal);
        Assert.Contains("'[data-agent=\"' + agent + '\"]'", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"kin\"", markup, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", markup, StringComparison.Ordinal);

        var layout = File.ReadAllText(
            RepositoryPath("src/Parley.Web/Pages/Shared/_Layout.cshtml"));

        Assert.Contains(".timeline.focused .tick:not(.kin)", layout, StringComparison.Ordinal);
        Assert.Contains("outline", layout, StringComparison.Ordinal);
    }

    /// <summary>Finds a path in the repository from wherever the tests run.</summary>
    /// <param name="relative">The path, relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 }
               && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
