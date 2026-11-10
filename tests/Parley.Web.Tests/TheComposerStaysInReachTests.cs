using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>The box for writing stays on screen from anywhere in a conversation, and a person can hide it to read.</summary>
/// <remarks>
/// What a stylesheet and a script then do needs a browser, which these tests do not have. They hold what the page must
/// send for that to work, including the rule that once cancelled the pinning without anything noticing.
/// </remarks>
[Integration]
public sealed partial class TheComposerStaysInReachTests
{
    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"\.composer\s*\{\s*position:\s*sticky;\s*bottom:\s*0")]
    private static partial Regex Pinned();

    // Any later rule for the form that names a position, which would win over the pinning.
    [GeneratedRegex(@"(form\.composer|#composer)\s*\{[^}]*position")]
    private static partial Regex RepositionedAgain();

    [GeneratedRegex("""<form method="post" id="composer"[^>]*>""")]
    private static partial Regex ComposerForm();

    private static async Task<string> PageAsync()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        console.Broker.Request(1, "orders@test", "human-user@" + TranscriptConsole.Project, "Gross or net?");

        return await client.GetStringAsync(PageUri, Ct);
    }

    [Fact]
    public async Task The_composer_is_pinned_and_nothing_takes_the_pinning_back()
    {
        var page = await PageAsync();

        Assert.Matches(Pinned(), page);
        Assert.DoesNotMatch(RepositionedAgain(), page);
    }

    [Fact]
    public async Task Typing_in_the_pinned_box_does_not_scroll_a_reader_who_scrolled_up()
    {
        // The browser keeps the caret clear of the page's scroll padding as the person types. Padding the height of the
        // pinned box put the box's own caret inside it, so each keystroke scrolled the page a line and the box, pinned,
        // never got clear. Seen in Edge: 300, 404, 505, 608, 711, 814 over five keys. The room is kept as a margin on
        // everything but the box.
        var page = await PageAsync();

        Assert.DoesNotMatch(@"scroll-padding(-bottom|-block)?\s*:\s*[^;]*composer-h", page);
        Assert.Contains(".page :not(.composer, .composer *) { scroll-margin-bottom: calc(var(--composer-h, 0px) + 1rem); }", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_form_only_a_script_submits_draws_no_empty_card_below_it()
    {
        var page = await PageAsync();

        Assert.Contains("id=\"close-request\"", page, StringComparison.Ordinal);
        Assert.Contains("form[hidden] { display: none; }", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_button_starts_hidden_so_a_page_with_no_script_keeps_the_box_open()
    {
        var page = await PageAsync();
        var button = Regex.Match(page, """<button[^>]*id="composer-toggle"[^>]*>""", RegexOptions.None, TimeSpan.FromSeconds(2)).Value;

        Assert.Contains(" hidden", button, StringComparison.Ordinal);
        Assert.DoesNotContain("composer-closed\"", ComposerForm().Match(page).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hiding_leaves_the_button_and_who_it_would_go_to_on_screen()
    {
        var page = await PageAsync();
        var bar = page.IndexOf("class=\"composer-bar\"", StringComparison.Ordinal);
        var box = page.IndexOf("name=\"Says\"", StringComparison.Ordinal);

        Assert.Contains(".composer-closed > :not(.composer-bar) { display: none; }", page, StringComparison.Ordinal);
        Assert.InRange(page.IndexOf("id=\"to-summary\"", StringComparison.Ordinal), bar, box);
        Assert.InRange(page.IndexOf("id=\"composer-toggle\"", StringComparison.Ordinal), bar, box);
    }

    [Fact]
    public async Task A_plain_load_draws_no_icon_for_what_was_sent()
    {
        // Without SentAsAsked needing something sent, every load of a conversation drew the "what happened" icon over an
        // empty list, since nothing sent is everything gone as asked (review, lens 3 round 32).
        var page = await PageAsync();

        Assert.DoesNotContain("class=\"sent-info\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<ul class=\"sent\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_earlier_choice_to_hide_may_close_a_page_with_nothing_to_say()
    {
        Assert.Contains("data-keep-open=\"false\"", ComposerForm().Match(await PageAsync()).Value, StringComparison.Ordinal);
    }

    private static RoadmapConsole Seated()
    {
        var console = new RoadmapConsole();

        console.Broker.Seats = true;
        console.Broker.Registry.Add(new Parley.Transport.RegistryRow
        {
            Name = "human-user@" + RoadmapConsole.Project,
            Adapter = "parley-console",
            Tier = Parley.Transport.AgentTier.Full,
            Status = Parley.Transport.AgentStatus.Active,
        });
        console.Broker.Registry.Add(new Parley.Transport.RegistryRow
        {
            Name = "orders@test",
            Adapter = "mcp",
            Tier = Parley.Transport.AgentTier.Full,
            Status = Parley.Transport.AgentStatus.Active,
        });

        return console;
    }

    private static async Task<string> SentAsync(RoadmapConsole console)
    {
        using var client = console.CreateClient();
        using var response = await NoJoinOnTheWebTests.PostAsync(
            client,
            $"/Conversations/Detail?project={RoadmapConsole.Project}&id=shared",
            new()
            {
                ["Targets"] = "orders@test",
                ["Says"] = "my own words",
            });

        return await response.Content.ReadAsStringAsync(Ct);
    }

    [Fact]
    public async Task A_send_that_went_as_asked_is_told_behind_an_icon_and_does_not_hold_the_box_open()
    {
        using var console = Seated();

        var page = await SentAsync(console);
        var start = page.IndexOf("class=\"sent-info\"", StringComparison.Ordinal);

        Assert.True(start > 0, "no icon");

        var behind = page[start..page.IndexOf("</details>", start, StringComparison.Ordinal)];

        Assert.Contains("<summary", behind, StringComparison.Ordinal);
        Assert.Contains("Sent.", behind[behind.IndexOf("<ul class=\"sent\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Equal(2, page.Split("<ul class=\"sent\"").Length);
        Assert.DoesNotContain("<details class=\"sent-info\" open", page, StringComparison.Ordinal);
        Assert.Contains("data-keep-open=\"false\"", ComposerForm().Match(page).Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Parley.Transport.WakeOutcome.Unreachable)]
    [InlineData(Parley.Transport.WakeOutcome.OpenOutside)]
    public async Task A_send_nothing_can_act_on_stays_in_plain_view(Parley.Transport.WakeOutcome wake)
    {
        using var console = Seated();
        console.Broker.RequestWake = wake;

        var page = await SentAsync(console);

        Assert.DoesNotContain("class=\"sent-info\"", page, StringComparison.Ordinal);
        Assert.Contains("<ul class=\"sent\"", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Parley.Transport.WakeOutcome.Started)]
    [InlineData(Parley.Transport.WakeOutcome.AlreadyRunning)]
    public async Task The_routine_sends_go_behind_the_icon(Parley.Transport.WakeOutcome wake)
    {
        using var console = Seated();
        console.Broker.RequestWake = wake;

        Assert.Contains("class=\"sent-info\"", await SentAsync(console), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_send_that_was_refused_stays_in_plain_view_and_holds_the_box_open()
    {
        using var console = Seated();
        console.Broker.RefusesRequestsTo.Add("orders@test");

        var page = await SentAsync(console);

        Assert.DoesNotContain("class=\"sent-info\"", page, StringComparison.Ordinal);
        Assert.Contains("<ul class=\"sent\"", page, StringComparison.Ordinal);
        Assert.Contains("data-keep-open=\"true\"", ComposerForm().Match(page).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_went_wrong_with_a_send_and_the_words_still_to_send_are_never_hidden()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await NoJoinOnTheWebTests.PostAsync(
            client,
            $"/Conversations/Detail?project={DeadBrokerConsole.Project}&id=any",
            new()
            {
                ["Targets"] = $"orders@{DeadBrokerConsole.Project}",
                ["Says"] = "anything at all",
            });

        var page = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("Nothing was sent", page, StringComparison.Ordinal);
        Assert.Contains("data-keep-open=\"true\"", ComposerForm().Match(page).Value, StringComparison.Ordinal);
    }
}
