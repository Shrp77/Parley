namespace Parley.Screens;

/// <summary>What to capture: each shot's file name, its exact size, the page and what to do on it first.</summary>
/// <param name="Theme">The console's theme for every shot: <c>dark</c> or <c>light</c>.</param>
/// <param name="Scale">The device scale factor: 1 for a PNG whose pixels are CSS pixels.</param>
/// <param name="MaximumBytes">The largest a PNG may be once its metadata is stripped; 0 for no limit.</param>
/// <param name="Shots">The shots, in the order they are taken.</param>
/// <remarks>
/// A file for the same reason the scenario is: the list changes every round and the code should not. A shot names its
/// page by path with placeholders (<see cref="Placeholders"/>), so it says what to show in the scenario's own words
/// rather than in numbers that move when the scenario changes.
/// </remarks>
internal sealed record ShotList(string Theme, int Scale, int MaximumBytes, IReadOnlyList<Shot> Shots)
{
    /// <summary>Reads a shot list, refusing anything it does not understand.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The list.</returns>
    /// <exception cref="InvalidDataException">The file is not a usable list; the message says why.</exception>
    public static ShotList Load(string path)
    {
        var list = Json.Read<ShotList>(path);
        var problems = new List<string>();

        if (list.Theme is not ("dark" or "light"))
        {
            problems.Add("theme must be dark or light");
        }

        if (list.Scale is < 1 or > 4)
        {
            problems.Add("scale must be 1 to 4");
        }

        foreach (var shot in list.Shots)
        {
            // A drive-relative name such as C:x.png has no separator and would still land outside the output folder.
            if (!shot.File.EndsWith(".png", StringComparison.Ordinal) || shot.File.IndexOfAny(['/', '\\', ':']) >= 0
                || !string.Equals(Path.GetFileName(shot.File), shot.File, StringComparison.Ordinal) || Path.IsPathRooted(shot.File))
            {
                problems.Add($"{shot.File}: a file name ending .png, with no folder or drive");
            }

            // The Files page's Open writes a copy into this machine's own application data, whatever the deployment,
            // and opens it on the desktop. Nothing a shot needs is behind a click there.
            if (shot.Path.StartsWith("/Projects/Files", StringComparison.OrdinalIgnoreCase) && (shot.Actions ?? []).Any(a => a.Click is not null))
            {
                problems.Add($"{shot.File}: no clicks on the Files page, whose Open writes into this machine's own copies folder");
            }

            if (shot.Width is < 200 or > 4000 || shot.Height is < 200 or > 6000)
            {
                problems.Add($"{shot.File}: width 200 to 4000 and height 200 to 6000");
            }

            if (!shot.Path.StartsWith('/'))
            {
                problems.Add($"{shot.File}: path must start with /");
            }

            foreach (var action in shot.Actions ?? [])
            {
                if (action.Problem() is { } problem)
                {
                    problems.Add($"{shot.File}: {problem}");
                }
            }
        }

        foreach (var twice in list.Shots.GroupBy(s => s.File, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"{twice.Key}: listed twice");
        }

        return problems.Count == 0
            ? list
            : throw new InvalidDataException($"{path}:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", problems));
    }

    /// <summary>Says what the shots name that <paramref name="scenario"/> does not make.</summary>
    /// <param name="scenario">The round's scenario.</param>
    /// <returns>One line per problem; none when every shot's path resolves.</returns>
    public IEnumerable<string> Against(Scenario scenario) =>
        Shots.SelectMany(shot => Placeholders.Problems(shot.Path, scenario).Select(problem => $"{shot.File}: {problem}"));
}

/// <summary>One screenshot.</summary>
/// <param name="File">The PNG's file name, exactly as the site expects it.</param>
/// <param name="Width">Its width in CSS pixels, which is also the browser window's.</param>
/// <param name="Height">Its height in CSS pixels, which is also the browser window's.</param>
/// <param name="Path">The console page, with placeholders.</param>
/// <param name="Shows">What it must show, in words, for whoever checks it.</param>
/// <param name="Graph">Whether the graph panel is pinned open beside the page.</param>
/// <param name="Storage">
/// Further entries for the browser's local storage before the page loads, as a person's own choices would be kept: for
/// example <c>"parley.nav": "collapsed"</c> to fold the navigation rail.
/// </param>
/// <param name="Actions">What to do on the page before the capture, in order.</param>
internal sealed record Shot(
    string File,
    int Width,
    int Height,
    string Path,
    string? Shows = null,
    bool Graph = false,
    IReadOnlyDictionary<string, string>? Storage = null,
    IReadOnlyList<ShotAction>? Actions = null);

/// <summary>One thing done on a page before its capture. Exactly one member is set.</summary>
/// <param name="Click">A selector to click.</param>
/// <param name="Wait">A selector to wait for until it is visible.</param>
/// <param name="Scroll">A selector to scroll into view, or <c>top</c>.</param>
/// <param name="Hide">A selector whose elements are hidden, such as a notice that would date the shot.</param>
/// <param name="Pause">Milliseconds to wait, for something that settles with no element to wait on.</param>
/// <remarks>
/// Selectors are Playwright's: CSS by default, and <c>text=</c>, <c>role=</c> and the others. A selector that matches
/// nothing fails the shot after <see cref="Camera.ActionTimeout"/>, saying which, rather than capturing the wrong page.
/// </remarks>
internal sealed record ShotAction(string? Click = null, string? Wait = null, string? Scroll = null, string? Hide = null, int Pause = 0)
{
    /// <summary>Says what is wrong with the action, or null.</summary>
    /// <returns>The problem, or null.</returns>
    public string? Problem()
    {
        var set = new[] { Click, Wait, Scroll, Hide }.Count(s => s is not null) + (Pause > 0 ? 1 : 0);

        return set == 1 ? (Pause > 5000 ? "pause at most 5000 ms" : null) : "an action sets exactly one of click, wait, scroll, hide, pause";
    }
}
