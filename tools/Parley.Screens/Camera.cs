using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Playwright;

namespace Parley.Screens;

/// <summary>A browser that takes the shots: one fresh page for each, at the shot's exact size.</summary>
/// <remarks>
/// <para>
/// <b>A browser already on the machine</b>, chosen by channel (<c>msedge</c> or <c>chrome</c>), so the tool downloads
/// nothing. <c>chromium</c> uses Playwright's own build, once <c>playwright install chromium</c> has fetched it.
/// </para>
/// <para>
/// Each shot gets a new browser context, so nothing one page stored, a draft or a scroll position, reaches the next.
/// The theme and the graph panel are set in the context's storage before the page's first script reads them, which is
/// how a person's own choice would arrive. Animations are stopped and the caret hidden, so a shot does not depend on
/// the moment it was taken.
/// </para>
/// </remarks>
internal sealed class Camera : IAsyncDisposable
{
    /// <summary>How long an action or a page load may take before the shot fails.</summary>
    public const int ActionTimeout = 15_000;

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;

    private Camera(IPlaywright playwright, IBrowser browser)
    {
        _playwright = playwright;
        _browser = browser;
    }

    /// <summary>Starts a browser.</summary>
    /// <param name="channel"><c>msedge</c>, <c>chrome</c> or <c>chromium</c>.</param>
    /// <returns>The camera.</returns>
    public static async Task<Camera> StartAsync(string channel)
    {
        var playwright = await Playwright.CreateAsync().ConfigureAwait(false);

        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Channel = string.Equals(channel, "chromium", StringComparison.Ordinal) ? null : channel,
            }).ConfigureAwait(false);

            return new Camera(playwright, browser);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    /// <summary>Captures an HTML file as an opaque PNG, for an image a scenario places.</summary>
    /// <param name="html">The file.</param>
    /// <param name="width">The image's width.</param>
    /// <param name="height">The image's height.</param>
    /// <returns>The PNG, stripped.</returns>
    public async Task<byte[]> RenderAsync(string html, int width, int height)
    {
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
            DeviceScaleFactor = 1,
            JavaScriptEnabled = false,
        }).ConfigureAwait(false);
        await using var lifetime = context.ConfigureAwait(false);

        // The page draws an image from its own folder and nothing else: no fetch leaves the machine while it renders.
        await context.RouteAsync("**/*", route =>
            route.Request.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? route.ContinueAsync() : route.AbortAsync()).ConfigureAwait(false);

        var page = await context.NewPageAsync().ConfigureAwait(false);

        if (await page.GotoAsync(new Uri(Path.GetFullPath(html)).AbsoluteUri).ConfigureAwait(false) is { Ok: false } refused)
        {
            throw new InvalidDataException($"{html} did not load: {refused.Status}");
        }

        return Png.Strip(await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png }).ConfigureAwait(false));
    }

    /// <summary>Takes one shot.</summary>
    /// <param name="list">The list it belongs to, for the theme and scale.</param>
    /// <param name="shot">The shot.</param>
    /// <param name="url">The page, placeholders already filled in.</param>
    /// <returns>The PNG, stripped and checked for size.</returns>
    /// <exception cref="InvalidDataException">The capture is not the size or weight the list asks for.</exception>
    public async Task<byte[]> TakeAsync(ShotList list, Shot shot, Uri url)
    {
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = shot.Width, Height = shot.Height },
            DeviceScaleFactor = list.Scale,
            ColorScheme = string.Equals(list.Theme, "dark", StringComparison.Ordinal) ? ColorScheme.Dark : ColorScheme.Light,
            ReducedMotion = ReducedMotion.Reduce,

            // The machine's own time zone and locale, deliberately: the console writes times on the server in the
            // machine's zone, and a page's script writes others in the browser's, so a browser in another zone would
            // put two clocks on one screen.
        }).ConfigureAwait(false);
        await using var lifetime = context.ConfigureAwait(false);

        context.SetDefaultTimeout(ActionTimeout);

        var entries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["parley.theme"] = list.Theme,
            ["parley.graph"] = JsonSerializer.Serialize(new { pinned = shot.Graph, open = shot.Graph }),
        };

        foreach (var (key, value) in shot.Storage ?? new Dictionary<string, string>())
        {
            entries[key] = value;
        }

        var storage = JsonSerializer.Serialize(entries);

        await context.AddInitScriptAsync(
            $"(() => {{ const s = {storage}; for (const k in s) {{ try {{ localStorage.setItem(k, s[k]); }} catch (e) {{ }} }} }})();")
            .ConfigureAwait(false);

        var page = await context.NewPageAsync().ConfigureAwait(false);

        // A page renamed in the console answers 404 with a page of its own, which would otherwise be captured at the
        // right size and pass every other check.
        var response = await page.GotoAsync(url.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load }).ConfigureAwait(false);

        if (response is not { Ok: true })
        {
            throw new InvalidDataException($"{shot.File}: {url.PathAndQuery} answered {response?.Status.ToString(CultureInfo.InvariantCulture) ?? "nothing"}");
        }

        // The storage keys are the console's own; if it renames one, the theme silently stops applying. Checked here
        // for the theme, the one every shot depends on.
        var theme = await page.EvaluateAsync<string?>("document.documentElement.getAttribute('data-theme')").ConfigureAwait(false);

        if (!string.Equals(theme, list.Theme, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{shot.File}: the page drew theme '{theme}', not '{list.Theme}'; has the console's parley.theme key changed?");
        }

        foreach (var action in shot.Actions ?? [])
        {
            await DoAsync(page, action).ConfigureAwait(false);
        }

        // Web fonts are part of the page: a shot taken before they load is in the fallback face.
        _ = await page.EvaluateAsync("document.fonts.ready.then(() => true)").ConfigureAwait(false);

        var png = Png.Strip(await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Type = ScreenshotType.Png,
            Animations = ScreenshotAnimations.Disabled,
            Caret = ScreenshotCaret.Hide,
        }).ConfigureAwait(false));

        var (width, height) = Png.SizeOf(png);

        if (width != shot.Width * list.Scale || height != shot.Height * list.Scale)
        {
            throw new InvalidDataException($"{shot.File}: captured {width}x{height}, listed {shot.Width}x{shot.Height} at scale {list.Scale}");
        }

        if (list.MaximumBytes > 0 && png.Length > list.MaximumBytes)
        {
            throw new InvalidDataException(string.Create(
                CultureInfo.InvariantCulture, $"{shot.File}: {png.Length:N0} bytes, over the list's {list.MaximumBytes:N0}"));
        }

        return png;
    }

    /// <summary>The lowercase hexadecimal SHA-256 of some bytes, as Parley names a placed file's.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The hash.</returns>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync().ConfigureAwait(false);
        _playwright.Dispose();
    }

    private static async Task DoAsync(IPage page, ShotAction action)
    {
        if (action.Click is { } click)
        {
            await page.Locator(click).First.ClickAsync().ConfigureAwait(false);
        }
        else if (action.Wait is { } wait)
        {
            await page.Locator(wait).First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible }).ConfigureAwait(false);
        }
        else if (action.Scroll is "top")
        {
            _ = await page.EvaluateAsync("window.scrollTo(0, 0)").ConfigureAwait(false);
        }
        else if (action.Scroll is { } scroll)
        {
            await page.Locator(scroll).First.ScrollIntoViewIfNeededAsync().ConfigureAwait(false);
        }
        else if (action.Hide is { } hide)
        {
            _ = await page.Locator(hide).EvaluateAllAsync<bool>("els => els.forEach(e => e.style.visibility = 'hidden') || true").ConfigureAwait(false);
        }
        else
        {
            await page.WaitForTimeoutAsync(action.Pause).ConfigureAwait(false);
        }
    }

    /// <summary>Formats a byte count for the run's report.</summary>
    /// <param name="bytes">The count.</param>
    /// <returns>Kilobytes, one decimal.</returns>
    public static string Kilobytes(long bytes) => (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
}
