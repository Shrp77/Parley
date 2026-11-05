using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Html;

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

        try
        {
            var document = Markdown.Parse(text, Pipeline);

            MakeSafe(document);

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
