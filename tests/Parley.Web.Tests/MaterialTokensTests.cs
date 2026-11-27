using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// The console's colours, type and fonts come from one set of Material 3 tokens (specs/035-material-3-web-ui), so a rule
/// that names a colour of its own is the console drifting back to looking like several products.
/// </summary>
[Integration]
public sealed partial class MaterialTokensTests
{
    private const string OlderNames = "/* The console's older names,";

    private static readonly string[] Roles =
    [
        "primary", "on-primary", "primary-container", "on-primary-container", "secondary", "on-secondary", "secondary-container",
        "on-secondary-container", "tertiary", "on-tertiary", "error", "on-error", "error-container", "on-error-container",
        "surface", "on-surface", "on-surface-variant", "surface-container-lowest", "surface-container-low", "surface-container",
        "surface-container-high", "surface-container-highest", "outline", "outline-variant", "inverse-surface",
        "inverse-on-surface", "inverse-primary",
    ];

    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(")]
    private static partial Regex AColour();

    private static string Layout => ScriptSource.Page("src/Parley.Web/Pages/Shared/_Layout.cshtml");

    private static string Light => Layout[..Layout.IndexOf("@@media (prefers-color-scheme: dark)", StringComparison.Ordinal)];

    private static string Dark
    {
        get
        {
            var start = Layout.IndexOf("@@media (prefers-color-scheme: dark)", StringComparison.Ordinal);

            return Layout[start..Layout.IndexOf(OlderNames, start, StringComparison.Ordinal)];
        }
    }

    [Fact]
    public void Every_colour_role_is_defined_for_light_and_for_dark()
    {
        foreach (var role in Roles)
        {
            Assert.Matches($"--md-sys-color-{role}: #[0-9a-f]{{6}};", Light);
            Assert.Matches($"--md-sys-color-{role}: #[0-9a-f]{{6}};", Dark);
        }
    }

    [Fact]
    public void Dark_chosen_on_the_settings_page_is_the_same_dark_the_system_gives()
    {
        static string Tokens(string block) =>
            string.Join('\n', block.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("--md-", StringComparison.Ordinal)));

        var system = Dark[..Dark.IndexOf(":root[data-theme=\"dark\"] {", StringComparison.Ordinal)];
        var chosen = Dark[Dark.IndexOf(":root[data-theme=\"dark\"] {", StringComparison.Ordinal)..];
        chosen = chosen[..chosen.IndexOf('}', StringComparison.Ordinal)];

        Assert.Contains(":root:not([data-theme=\"light\"]) {", system, StringComparison.Ordinal);
        Assert.NotEmpty(Tokens(chosen));
        Assert.Equal(Tokens(system), Tokens(chosen));
    }

    [Fact]
    public void The_colours_are_frost_as_the_person_chose()
    {
        // The person chose Direction B, Frost, on 2026-11-24 (specs/042-theme-and-layout-refresh/decision.md), in place of
        // the scheme seeded from #2b5cd9. Its colours come from directions/generate.py; this pins that they landed.
        Assert.Contains("--md-sys-color-primary-container: #cfe5fe;", Light, StringComparison.Ordinal);
        Assert.DoesNotContain("#2b5cd9", Light, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/Parley.Web/Pages/Shared/_Layout.cshtml")]
    [InlineData("src/Parley.Web/Pages/Conversations/Detail.cshtml")]
    [InlineData("src/Parley.Web/Pages/Index.cshtml")]
    [InlineData("src/Parley.Web/Pages/Projects/Enrol.cshtml")]
    public void No_rule_outside_the_tokens_names_a_colour_of_its_own(string page)
    {
        var text = ScriptSource.Page(page);
        var rules = page.EndsWith("_Layout.cshtml", StringComparison.Ordinal) ? text[text.IndexOf(OlderNames, StringComparison.Ordinal)..] : text;

        // The one exception: a <select>'s chevron, drawn as an image for browsers that cannot restyle the control, where
        // an image cannot read a custom property. It is a mid grey that reads in both themes.
        var offending = rules
            .Split('\n')
            .Where(line => !line.Contains("url(\"data:image/svg+xml", StringComparison.Ordinal))
            .Where(line => !line.TrimStart().StartsWith("--md-", StringComparison.Ordinal))
            .Where(line => AColour().IsMatch(line.Replace("href=\"#", string.Empty, StringComparison.Ordinal)
                .Replace("#m", string.Empty, StringComparison.Ordinal).Replace("#main", string.Empty, StringComparison.Ordinal)))
            .Where(line => !Regex.IsMatch(line, @"#[a-z][a-z-]*\s*\{|getElementById|querySelector|location\.hash|""#", RegexOptions.None, TimeSpan.FromSeconds(1)))
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void Text_is_set_in_roboto_flex_on_the_type_scale()
    {
        Assert.Contains("font-family: \"Roboto Flex\";", Layout, StringComparison.Ordinal);
        Assert.Contains("src: url(\"/brand/roboto-flex.woff2\")", Layout, StringComparison.Ordinal);
        Assert.Matches(@"body \{\s*font: var\(--md-sys-typescale-body-medium\);", Layout);
    }

    [Theory]
    [InlineData("/brand/roboto-flex.woff2", "font/woff2")]
    [InlineData("/brand/roboto-flex-license.txt", "text/plain")]
    [InlineData("/brand/tokens.css", "text/css")]
    [InlineData("/brand/components.css", "text/css")]
    public async Task The_typeface_its_licence_and_the_stylesheets_are_served_from_the_console_itself(string path, string type)
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith(type, response.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.True((await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length > 1000);
    }

    [Fact]
    public void Every_control_shows_material_3s_focus_indicator()
    {
        Assert.Contains(":focus-visible { outline: 3px solid var(--md-sys-color-secondary); outline-offset: 2px; }", Layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Buttons_hover_press_and_focus_through_state_layers()
    {
        foreach (var state in new[] { "hover", "focus", "pressed" })
        {
            Assert.Contains($"color-mix(in srgb, currentColor var(--md-sys-state-{state}), transparent)", Layout, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("filter: brightness", Layout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/Parley.Web/Pages/Conversations/Destroy.cshtml")]
    [InlineData("src/Parley.Web/Pages/Projects/Files.cshtml")]
    public void A_destructive_act_is_filled_in_the_error_colour(string page)
    {
        Assert.Contains("class=\"danger act\"", File.ReadAllText(RepositoryPath(page)), StringComparison.Ordinal);
        Assert.Contains("button.danger.act, .button.danger.act { --md-btn-container: var(--md-sys-color-error);", Layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_are_in_sentence_case()
    {
        var components = Layout[Layout.IndexOf("/* ---- Material 3 components", StringComparison.Ordinal)..];

        Assert.Matches(@"th, \.dgrid-head, \.grid-head[^{]*\{\s*text-transform: none;", components);
    }

    [Fact]
    public void Motion_stops_for_a_person_who_asks_for_less()
    {
        Assert.Matches(@"@@media \(prefers-reduced-motion: reduce\) \{\s*:root \{ --md-sys-motion-duration-short: 0ms;", Layout);
    }

    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 } && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
