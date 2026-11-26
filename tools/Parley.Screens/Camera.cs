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
    /// <param name="overlay">
    /// CSS laid over the page after it loads, such as a design direction's tokens (<c>--stylesheet</c>), or null for the
    /// console as it ships.
    /// </param>
    /// <returns>The PNG, stripped and checked for size.</returns>
    /// <exception cref="InvalidDataException">The capture is not the size or weight the list asks for.</exception>
    public async Task<byte[]> TakeAsync(ShotList list, Shot shot, Uri url, string? overlay = null)
    {
        var (page, lifetime) = await OpenAsync(list, shot, url, overlay, phone: false).ConfigureAwait(false);
        await using var _ = lifetime.ConfigureAwait(false);

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

    /// <summary>Checks a shot's page for what a phone would get wrong: anything wider than the window, and controls
    /// nobody can reach.</summary>
    /// <param name="list">The list it belongs to.</param>
    /// <param name="shot">The shot.</param>
    /// <param name="url">The page, placeholders already filled in.</param>
    /// <param name="overlay">CSS laid over the page, such as larger text, or null.</param>
    /// <returns>One line per problem; none when the page fits.</returns>
    /// <remarks>
    /// A desktop browser clips what the page hides with <c>overflow-x: hidden</c>, so a screenshot of a page that
    /// overflows can look right. A phone's browser does not always: it widens the page and the person scrolls sideways
    /// to reach what is pinned to the right edge. So this measures every element's box rather than trusting the
    /// picture, and leaves out only what sits inside a box of its own that scrolls within the window. A control
    /// is unreachable when, scrolled to the middle of the window, its centre is outside the window, cut off by a box
    /// around it, or under something that stays put while the page scrolls, such as a bar. Ordinary content over it is
    /// not counted, because scrolling moves it away.
    /// </remarks>
    public async Task<IReadOnlyList<string>> AuditAsync(ShotList list, Shot shot, Uri url, string? overlay = null)
    {
        var (page, lifetime) = await OpenAsync(list, shot, url, overlay, phone: Math.Min(shot.Width, shot.Height) < 600).ConfigureAwait(false);
        await using var _ = lifetime.ConfigureAwait(false);

        return await page.EvaluateAsync<string[]>(AuditScript).ConfigureAwait(false);
    }

    private const string AuditScript = """
        () => {
          document.documentElement.style.scrollBehavior = 'auto';
          // The device's own width: a phone widens the layout to fit a page that overflows, so the layout's width
          // would hide the very fault this looks for.
          const vw = Math.min(document.documentElement.clientWidth, screen.width);
          const vh = window.innerHeight;
          const name = e => {
            let s = e.tagName.toLowerCase();
            if (e.id) s += '#' + e.id;
            for (const c of e.classList) s += '.' + c;
            const t = (e.getAttribute('aria-label') || e.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 40);
            return t ? `${s} "${t}"` : s;
          };
          const shown = e => e.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true });
          // The nearest box that scrolls or clips what it holds, short of the page itself.
          const box = e => {
            for (let p = e.parentElement; p && p !== document.body && p !== document.documentElement; p = p.parentElement) {
              const s = getComputedStyle(p);
              if (s.overflowX !== 'visible' || s.contain.includes('paint')) return p;
            }
            return null;
          };
          // A person can scroll a box that scrolls; what a box clips is gone.
          const clips = p => { const o = getComputedStyle(p).overflowX; return o === 'hidden' || o === 'clip' || (o === 'visible' && getComputedStyle(p).contain.includes('paint')); };
          // Every box around a control that cuts off what it holds, on either axis.
          const hides = p => ['overflowX', 'overflowY'].some(a => ['hidden', 'clip'].includes(getComputedStyle(p)[a]));
          const clippers = e => { const all = []; for (let p = e.parentElement; p && p !== document.body && p !== document.documentElement; p = p.parentElement) if (hides(p)) all.push(p); return all; };
          // Parked off the page until focused, as a skip link is: not part of the layout.
          const parked = e => e.closest('.visually-hidden, .skip');
          // The nearest box that stays put while the page scrolls: a bar, a sheet, a pinned strip.
          const pinned = e => {
            for (let p = e; p && p !== document.documentElement; p = p.parentElement) {
              const pos = getComputedStyle(p).position;
              if (pos === 'fixed' || pos === 'sticky') return p;
            }
            return null;
          };
          const out = [];
          const page = document.documentElement;
          if (page.scrollWidth > vw + 1 || page.clientWidth > vw + 1 || (window.visualViewport && window.visualViewport.scale < 0.99)) {
            out.push(`the page is ${page.scrollWidth} wide in a ${vw} window (layout ${page.clientWidth}, zoom ${window.visualViewport ? window.visualViewport.scale.toFixed(2) : '?'})`);
          }
          const wide = new Set();
          const cut = new Set();
          for (const e of document.body.querySelectorAll('*')) {
            if (!shown(e) || parked(e)) continue;
            const r = e.getBoundingClientRect();
            if (r.width < 1 || r.height < 1) continue;
            const b = box(e);
            if (!b) {
              if (r.right > vw + 1 || r.left < -1) {
                wide.add(e);
                if (!wide.has(e.parentElement)) out.push(`wider than the window (${Math.round(r.left)}..${Math.round(r.right)} of ${vw}): ${name(e)}`);
              }
              continue;
            }
            // Text cut short with an ellipsis is clipped on purpose, and so is what a box scrolls. A box a pixel or two
            // wide is there for a screen reader only.
            const c = b.getBoundingClientRect();
            if (!clips(b) || getComputedStyle(b).textOverflow === 'ellipsis' || cut.has(b) || c.width <= 2) continue;
            if (r.right > c.right + 1 || r.left < c.left - 1) {
              cut.add(b);
              out.push(`cut off on the side by ${name(b)} (${Math.round(r.left)}..${Math.round(r.right)} in ${Math.round(c.left)}..${Math.round(c.right)}): ${name(e)}`);
            }
          }
          // A box that scrolls sideways is either meant to (a code block, a wide table, the timeline) or is the fault
          // itself, the page's column being pushed wider than the window. Each is listed, for a person to judge.
          for (const e of document.body.querySelectorAll('*')) {
            if (!shown(e) || parked(e)) continue;
            const o = getComputedStyle(e).overflowX;
            if ((o === 'auto' || o === 'scroll') && e.scrollWidth > e.clientWidth + 1 && e.clientWidth > 2) {
              out.push(`scrolls sideways (${e.scrollWidth} in ${e.clientWidth}): ${name(e)}`);
            }
          }
          const scroller = document.scrollingElement;
          const controls = document.body.querySelectorAll('a[href], button, summary, input:not([type=hidden]), select, textarea, [role=button], [tabindex]:not([tabindex="-1"])');
          // A sheet over the whole window, such as the graph panel on a phone, covers the page on purpose; only what is
          // on the sheet must be reachable then.
          // It is the fixed box drawn at the window's centre whose part inside the window fills most of it; measured on
          // the screen, so a drawer moved out of sight or a box drawn under another is not taken for one.
          const middle = document.elementFromPoint(vw / 2, vh / 2);
          let sheet = null;
          for (let p = middle; p && p !== document.documentElement; p = p.parentElement) {
            if (getComputedStyle(p).position !== 'fixed') continue;
            const r = p.getBoundingClientRect();
            const seen = Math.max(0, Math.min(r.right, vw) - Math.max(r.left, 0)) * Math.max(0, Math.min(r.bottom, vh) - Math.max(r.top, 0));
            if (seen >= 0.8 * vw * vh) { sheet = p; break; }
          }
          // Where every box that cuts off what it holds was scrolled when the page settled, which is what a person sees.
          const settled = new Map();
          for (const p of document.body.querySelectorAll('*')) if (hides(p)) settled.set(p, [p.scrollLeft, p.scrollTop]);
          for (const e of controls) {
            if (!shown(e) || e.disabled || parked(e) || (sheet && !sheet.contains(e))) continue;
            e.scrollIntoView({ block: 'center', inline: 'center' });
            // A script may scroll a box that clips, but a person cannot: put each one back before measuring.
            const held = clippers(e);
            for (const p of held) { const [left, top] = settled.get(p) || [0, 0]; p.scrollLeft = left; p.scrollTop = top; }
            const r = e.getBoundingClientRect();
            if (r.width < 1 || r.height < 1) continue;
            const x = r.left + r.width / 2, y = r.top + r.height / 2;
            if (x < 0 || x > vw || y < 0 || y > vh) {
              out.push(`out of reach (${Math.round(x)},${Math.round(y)} in ${vw}x${vh}): ${name(e)}`);
              continue;
            }
            const clippedBy = held.find(p => { const c = p.getBoundingClientRect(); return x < c.left || x > c.right || y < c.top || y > c.bottom; });
            if (clippedBy) {
              out.push(`cut off by ${name(clippedBy)}: ${name(e)}`);
              continue;
            }
            const top = document.elementFromPoint(x, y);
            // Only what stays put can hide a control for good: a page's own content scrolls away from it. A menu or
            // popup over the bar it opened from covers that bar on purpose.
            const over = top && pinned(top);
            if (top && over && top !== e && !e.contains(top) && !top.contains(e) && !(e.labels && [...e.labels].some(l => l.contains(top)))
                && over !== pinned(e) && !over.contains(e)) {
              out.push(`covered by ${name(top)}: ${name(e)}`);
            }
          }
          scroller.scrollTo(0, 0);
          return out;
        }
        """;

    private async Task<(IPage Page, IAsyncDisposable Lifetime)> OpenAsync(ShotList list, Shot shot, Uri url, string? overlay, bool phone)
    {
        var wanted = shot.ThemeIn(list);
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = shot.Width, Height = shot.Height },
            DeviceScaleFactor = list.Scale,
            ColorScheme = string.Equals(wanted, "dark", StringComparison.Ordinal) ? ColorScheme.Dark : ColorScheme.Light,
            ReducedMotion = ReducedMotion.Reduce,

            // As a phone's browser lays a page out: it honours the viewport tag, and a page wider than the window
            // widens the layout and is scrolled sideways rather than clipped.
            IsMobile = phone,
            HasTouch = phone,

            // The machine's own time zone and locale, deliberately: the console writes times on the server in the
            // machine's zone, and a page's script writes others in the browser's, so a browser in another zone would
            // put two clocks on one screen.
        }).ConfigureAwait(false);

        try
        {
            return (await PrepareAsync(context, shot, wanted, url, overlay).ConfigureAwait(false), context);
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IPage> PrepareAsync(IBrowserContext context, Shot shot, string wanted, Uri url, string? overlay)
    {
        context.SetDefaultTimeout(ActionTimeout);

        var entries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["parley.theme"] = wanted,
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

        if (!string.Equals(theme, wanted, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{shot.File}: the page drew theme '{theme}', not '{wanted}'; has the console's parley.theme key changed?");
        }

        // Last in the head, so its custom properties win over the console's own at the same specificity.
        if (overlay is not null)
        {
            _ = await page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = overlay }).ConfigureAwait(false);
        }

        foreach (var action in shot.Actions ?? [])
        {
            await DoAsync(page, action).ConfigureAwait(false);
        }

        // Web fonts are part of the page: a shot taken before they load is in the fallback face.
        _ = await page.EvaluateAsync("document.fonts.ready.then(() => true)").ConfigureAwait(false);

        return page;
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
