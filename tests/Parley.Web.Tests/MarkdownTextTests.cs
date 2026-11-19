using System.Diagnostics;
using System.Text.Encodings.Web;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>An agent's Markdown is formatted, and a peer's text can do nothing else to the page.</summary>
[Unit]
public sealed class MarkdownTextTests
{
    private static string Html(string text)
    {
        using var written = new StringWriter();

        MarkdownText.Render(text).WriteTo(written, HtmlEncoder.Default);

        return written.ToString();
    }

    [Theory]
    [InlineData("**strong**", "<strong>strong</strong>")]
    [InlineData("*leaning*", "<em>leaning</em>")]
    [InlineData("`code`", "<code>code</code>")]
    [InlineData("# Title", "<h1>Title</h1>")]
    [InlineData("- one\n- two", "<li>one</li>")]
    [InlineData("1. one\n2. two", "<ol>")]
    [InlineData("> quoted", "<blockquote>")]
    [InlineData("```\nblock\n```", "<pre><code>block")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |", "<td>1</td>")]
    [InlineData("first\nsecond", "<br")]
    public void Formatting_is_drawn(string text, string expected) =>
        Assert.Contains(expected, Html(text), StringComparison.Ordinal);

    [Theory]
    [InlineData("<script>alert(1)</script>", "<script")]
    [InlineData("<img src=x onerror=alert(1)>", "<img")]
    [InlineData("hello <b onclick=alert(1)>bold</b>", "<b ")]
    [InlineData("<style>body{display:none}</style>", "<style")]
    [InlineData("<form action=/x><button>go</button></form>", "<form")]
    [InlineData("<iframe src=http://example.com></iframe>", "<iframe")]
    public void Raw_html_is_only_words(string text, string never) =>
        Assert.DoesNotContain(never, Html(text), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Attributes_cannot_be_attached() =>
        Assert.DoesNotContain("onclick=", Html("# heading {onclick=alert(1)}\n\n[x](http://a.example){onclick=alert(1)}").Replace("{onclick=", "", StringComparison.Ordinal), StringComparison.Ordinal);

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JaVaScRiPt:alert(1))")]
    [InlineData("[click](&#x6A;avascript:alert(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click](vbscript:x)")]
    [InlineData("[click](/Projects/alpha/Files)")]
    [InlineData("[click](#m1)")]
    [InlineData("[click][r]\n\n[r]: javascript:alert(1)")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("<file:///c:/secret>")]
    [InlineData("<someone@example.com>")]
    public void Only_web_addresses_become_links(string text)
    {
        var html = Html(text);

        Assert.DoesNotContain("<a ", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_words_of_a_refused_link_remain() =>
        Assert.Contains("click", Html("[click](javascript:alert(1))"), StringComparison.Ordinal);

    [Theory]
    [InlineData("[site](https://example.com/a)", "https://example.com/a")]
    [InlineData("see https://example.com/b today", "https://example.com/b")]
    [InlineData("<http://example.com/c>", "http://example.com/c")]
    public void A_web_address_opens_apart_from_the_console(string text, string address)
    {
        var html = Html(text);

        Assert.Contains($"href=\"{address}\"", html, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("![chart](https://example.com/pixel.png)", "chart")]
    [InlineData("![](https://example.com/pixel.png)", "https://example.com/pixel.png")]
    [InlineData("![chart](javascript:alert(1))", "chart")]
    public void An_image_is_never_fetched(string text, string words)
    {
        var html = Html(text);

        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("src=", html, StringComparison.Ordinal);
        Assert.Contains(words, html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('>')]
    [InlineData('[')]
    [InlineData('*')]
    [InlineData('`')]
    [InlineData('-')]
    public void The_longest_message_allowed_of_one_character_neither_crashes_nor_stalls_the_page(char mark)
    {
        var clock = Stopwatch.StartNew();

        _ = Html(new string(mark, 512_000));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Deep_lists_do_not_crash_the_page()
    {
        var deep = string.Concat(Enumerable.Range(0, 2000).Select(i => new string(' ', i * 2) + "- x\n"));

        Assert.Contains("x", Html(deep), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_written_is_nothing_drawn() => Assert.Empty(Html(string.Empty));

    [Fact]
    public void A_reference_to_an_exchange_becomes_a_button_carrying_only_its_number()
    {
        var html = Html("Thanks for **the answers** on exchange #351.");

        Assert.Contains(
            "on exchange <button type=\"button\" class=\"exchange-ref\" data-exchange=\"351\" aria-expanded=\"false\" title=\"Show exchange #351 here\">#351</button>.",
            html,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("`exchange #351`")]
    [InlineData("```\nexchange #351\n```")]
    [InlineData("[exchange #351](https://example.com)")]
    [InlineData("on 351")]
    public void A_reference_in_code_a_link_or_any_other_form_stays_as_written(string text)
    {
        Assert.DoesNotContain("exchange-ref", Html(text), StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_inside_emphasis_in_a_links_words_stays_inside_the_link_as_written()
    {
        // The emphasis is not the link itself but sits inside it: a button there would be a control inside an anchor.
        var html = Html("[see *exchange #351*](https://example.com)");

        Assert.DoesNotContain("exchange-ref", html, StringComparison.Ordinal);
        Assert.Contains("<em>exchange #351</em></a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_past_the_formatting_bound_is_shown_as_written_and_drawn_at_once()
    {
        // Review, performance lens run 2: a run of unclosed links took 11 s to parse at 256,000 characters, and a followed
        // conversation draws every message again every few seconds.
        var text = string.Concat(Enumerable.Repeat("[a](", (MarkdownText.MaxFormatted / 4) + 1));
        var watch = Stopwatch.StartNew();

        var html = Html(text);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"drawing took {watch.Elapsed}");
        Assert.StartsWith("<pre>[a](", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_within_the_bound_is_still_formatted()
    {
        Assert.Contains("<strong>x</strong>", Html("**x**" + new string(' ', 100)), StringComparison.Ordinal);
    }
}
