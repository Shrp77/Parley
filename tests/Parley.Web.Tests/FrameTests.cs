using System.Net;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// The frame that stays on screen, and the brand it carries.
/// </summary>
/// <remarks>
/// <para>
/// A sticky header is four rules and not one. Three other things stick underneath it — the
/// roster, the minimap, and where an anchor lands — and each has to be offset by what the header
/// occupies. Getting one of them wrong is silent: the column simply sits behind the header, or a
/// tick scrolls its message out of sight, and the page looks fine in a screenshot taken at the
/// top.
/// </para>
/// <para>
/// The brand is checked over HTTP rather than on disk, because on disk is not where it has ever
/// gone wrong. The stylesheet was a file beside the binary that did not survive <c>dotnet
/// pack</c>, and the console rendered unstyled from the installed tool while the build output was
/// perfect. These assert what a browser would receive.
/// </para>
/// </remarks>
public sealed class FrameTests
{
    private static string Layout =>
        File.ReadAllText(RepositoryPath("src/Parley.Web/Pages/Shared/_Layout.cshtml"));

    [Fact]
    public void The_frame_is_pinned_to_the_top()
    {
        var header = Layout[Layout.IndexOf("        header {", StringComparison.Ordinal)..];

        Assert.Contains("position: sticky", header[..300], StringComparison.Ordinal);
        Assert.Contains("top: 0", header[..300], StringComparison.Ordinal);

        // Opaque, or the transcript scrolls through it.
        Assert.Contains("background: var(--page)", header[..300], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".timeline {")]
    public void Everything_that_sticks_below_it_is_offset_by_what_it_occupies(string rule)
    {
        // Each was `top: 1rem`, which put it behind the header the moment it stopped
        // scrolling away.
        var block = Layout[Layout.IndexOf(rule, StringComparison.Ordinal)..];
        var top = block.IndexOf("top:", StringComparison.Ordinal);

        Assert.Contains("calc(var(--frame)", block[top..(top + 40)], StringComparison.Ordinal);
    }

    [Fact]
    public void An_anchor_lands_below_the_frame_rather_than_behind_it()
    {
        // The ticks on the minimap are anchors, and a tick that scrolls its message under the
        // header is a link that appears not to work at all.
        Assert.Contains(
            "li.turn { scroll-margin-top: calc(var(--frame)",
            Layout,
            StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_frame_occupies_is_measured_rather_than_assumed()
    {
        // The constant is right until the frame wraps to two lines, which it does on a narrow
        // screen — and then every offset above is wrong by however much it grew, with nothing
        // anywhere reporting it.
        Assert.Contains("ResizeObserver", Layout, StringComparison.Ordinal);
        Assert.Contains("'--frame'", Layout, StringComparison.Ordinal);

        // And the stylesheet still carries a real figure, because that is what a reader with no
        // scripts keeps.
        Assert.Contains("--frame: 3.6rem", Layout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/brand/icon.png")]
    [InlineData("/brand/wordmark.png")]
    [InlineData("/favicon.ico")]
    public async Task The_brand_is_served_from_inside_the_assembly(string path)
    {
        // The assertion that matters is that the bytes arrive. A resource renamed, excluded from
        // the project file, or lost to packing gives a 404 that shows as a page with no logo —
        // which nobody would call a fault, and which is exactly how the stylesheet was lost.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(bytes);

        // A PNG, and not an error page with a hopeful content type.
        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47], bytes[..4]);
    }

    [Fact]
    public async Task Nothing_but_the_brand_is_reachable_through_that_route()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri("/brand/appsettings.json", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_frame_names_the_icon_as_the_tab_icon()
    {
        // Otherwise a browser asks for /favicon.ico anyway, which is why that is mapped too.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(
            new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("rel=\"icon\"", body, StringComparison.Ordinal);
        Assert.Contains("/brand/icon.png", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mark_is_decorative_because_the_word_beside_it_says_the_same_thing()
    {
        // Alt text repeating the wordmark would have a screen reader say "Parley" twice, once
        // for the picture of the word and once for the word.
        Assert.Contains("src=\"/brand/icon.png\" alt=\"\"", Layout, StringComparison.Ordinal);

        // Inverted rather than shipped twice, so the two can never disagree.
        Assert.Contains(".brand img { filter: invert(1); }", Layout, StringComparison.Ordinal);
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
