using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Html;
using Parley.Client.Rendering;

namespace Parley.Web;

/// <summary>Draws an agent's Markdown as formatted text, and allows it to do nothing else.</summary>
/// <remarks>
/// <para>
/// <b>The one place a message reaches the page as markup.</b> A payload is inert data written by a peer, so what it may
/// produce is decided here rather than left to the library's defaults, which allow raw HTML and any link scheme.
/// <c>RenderingTests</c> holds that no other file makes an <see cref="HtmlString"/>.
/// </para>
/// <para>
/// <b>Raw HTML is shown as the words it was written as</b>, and the extension that attaches attributes is not used, so
/// nothing a peer writes can become a script, a style or a form.
/// </para>
/// <para>
/// <b>Only http and https links, as <see cref="LinkedText"/> has it.</b> Any other link keeps its words and loses the
/// link, including a relative one, which would otherwise aim at this console's own pages.
/// </para>
/// <para>
/// <b>An image is never fetched.</b> Loading one would tell whoever holds that address when this page was read, with no
/// act by the reader, so it becomes a link to follow by choice.
/// </para>
/// <para>
/// <b>A reference to an exchange becomes a button</b> (spec 036, FR-020): <c>exchange #351</c>, in the one form
/// <see cref="ExchangeReferences"/> reads, opens that exchange under the line it is on. Only in plain text: code and the
/// words of a link stay as written. The button carries the number and nothing else the agent wrote, so it can do nothing
/// but ask this console to show that exchange.
/// </para>
/// </remarks>
public static class MarkdownText
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    /// <summary>The most characters that are formatted; a longer message is shown as the words it was written as.</summary>
    /// <remarks>
    /// The soft limit on a message, so no ordinary message reaches it. Markdown parsing is worse than linear on
    /// some input (a run of unclosed links took 0.7 s at 64,000 characters and 11 s at 256,000, and a crafted 32,768 still took about 0.27 s), a message may be 512,000
    /// long, and a followed conversation draws every message again every few seconds, so one such message would stall its page.
    /// </remarks>
    public const int MaxFormatted = 16_000;

    /// <summary>Formats one message's words.</summary>
    /// <param name="text">The words as written.</param>
    /// <returns>Markup safe to write to the page.</returns>
    /// <remarks>
    /// Text the parser refuses, such as nesting too deep to walk, is shown as plain encoded words. The words matter more
    /// than their formatting, and a message must not be able to take down the page that shows it.
    /// </remarks>
    public static IHtmlContent Render(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return HtmlString.Empty;
        }

        if (text.Length > MaxFormatted)
        {
            return new HtmlContentBuilder()
                .AppendHtml("<p><em>Too long to format, so shown as written.</em></p><pre>")
                .Append(text)
                .AppendHtml("</pre>");
        }

        try
        {
            var document = Markdown.Parse(text, Pipeline);

            MakeSafe(document);
            OpenReferences(document);

            using var written = new StringWriter();
            var renderer = new HtmlRenderer(written);

            Pipeline.Setup(renderer);
            renderer.Render(document);
            written.Flush();

            return new HtmlString(written.ToString());
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException)
        {
            return new HtmlContentBuilder().AppendHtml("<p>").Append(text).AppendHtml("</p>");
        }
    }

    private static void MakeSafe(MarkdownDocument document)
    {
        foreach (var inline in document.Descendants<AutolinkInline>().ToList())
        {
            if (Followable(inline.Url) is { } address)
            {
                inline.Url = address;
                Opens(inline);
            }
            else
            {
                inline.ReplaceBy(new LiteralInline(inline.Url));
            }
        }

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            var url = link.GetDynamicUrl?.Invoke() ?? link.Url;

            link.GetDynamicUrl = null;

            if (link.FirstChild is null)
            {
                link.AppendChild(new LiteralInline(url ?? string.Empty));
            }

            link.IsImage = false;

            if (Followable(url) is { } address)
            {
                link.Url = address;
                Opens(link);
            }
            else
            {
                WordsOnly(link);
            }
        }
    }

    /// <summary>Turns each reference to an exchange in plain text into a button that opens it.</summary>
    /// <param name="document">The parsed message, already made safe.</param>
    /// <remarks>
    /// Runs of plain text are joined before matching, because the parser may split one sentence into several pieces and a
    /// reference must not be missed for falling across two. Text inside a link is left alone: its words already go
    /// somewhere.
    /// </remarks>
    private static void OpenReferences(MarkdownDocument document)
    {
        // Each block's own top inline holds its text, and is not among its descendants, so it is taken first.
        var containers = document.Descendants<LeafBlock>()
            .Where(block => block.Inline is not null)
            .SelectMany(block => new[] { (ContainerInline)block.Inline! }.Concat(block.Inline!.Descendants<ContainerInline>()))
            .ToList();

        foreach (var container in containers)
        {
            if (container is LinkInline || HasLinkAbove(container))
            {
                continue;
            }

            var run = new List<LiteralInline>();

            foreach (var child in container.ToList())
            {
                if (child is LiteralInline literal)
                {
                    run.Add(literal);
                    continue;
                }

                Open(run);
                run = [];
            }

            Open(run);
        }
    }

    private static bool HasLinkAbove(Inline inline)
    {
        for (var parent = inline.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is LinkInline)
            {
                return true;
            }
        }

        return false;
    }

    private static void Open(List<LiteralInline> run)
    {
        if (run.Count == 0)
        {
            return;
        }

        var text = string.Concat(run.Select(literal => literal.Content.ToString()));
        var found = ExchangeReferences.Find(text);

        if (found.Count == 0)
        {
            return;
        }

        var at = 0;
        var anchor = run[^1];

        foreach (var reference in found)
        {
            if (reference.Start > at)
            {
                anchor.InsertBefore(new LiteralInline(text[at..reference.Start]));
            }

            // Digits only, so nothing the agent wrote reaches the markup but the number.
            var number = reference.Exchange.ToString(System.Globalization.CultureInfo.InvariantCulture);

            anchor.InsertBefore(new HtmlInline(
                $"<button type=\"button\" class=\"exchange-ref\" data-exchange=\"{number}\" aria-expanded=\"false\" " +
                $"title=\"Show exchange #{number} here\">#{number}</button>"));

            at = reference.Start + reference.Length;
        }

        if (at < text.Length)
        {
            anchor.InsertBefore(new LiteralInline(text[at..]));
        }

        foreach (var literal in run)
        {
            literal.Remove();
        }
    }

    private static void WordsOnly(LinkInline link)
    {
        var child = link.FirstChild;

        while (child is not null)
        {
            var next = child.NextSibling;

            child.Remove();
            link.InsertBefore(child);
            child = next;
        }

        link.Remove();
    }

    private static void Opens(Inline link)
    {
        var attributes = link.GetAttributes();

        attributes.AddPropertyIfNotExist("target", "_blank");
        attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
    }

    private static string? Followable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
}
