using System.Text.RegularExpressions;
using Grpc.Net.Client;
using Parley.Client;
using Parley.Core;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That every key this surface answers to is one a person could have found.
/// </summary>
/// <remarks>
/// <para>
/// <c>contracts/views.md</c>: <i>every key is listed where the person can find it, and no action
/// exists only as a key: whatever a key does, a pointer reaches too.</i> That promise was kept by
/// hand, one key at a time, and by hand is how it was broken twice in the same feature — Escape
/// went back from the day 006 was built and was named nowhere, and Ctrl+Q was named in a comment
/// and bound nowhere at all. Each was found by somebody using the thing, months apart.
/// </para>
/// <para>
/// <b>So the list of keys is read out of <c>OnKeyDown</c> rather than written here.</b> A test
/// carrying its own copy of the bindings passes forever after somebody adds a fifth: it would be
/// a second statement of the surface rather than a check on it, which is the failure this whole
/// promise keeps having.
/// </para>
/// </remarks>
public sealed class EveryKeyIsOfferedTests
{
    /// <summary>
    /// Keys that are navigation rather than actions, and why each needs no entry.
    /// </summary>
    /// <remarks>
    /// Listed by name so that adding to this list is a decision somebody makes, rather than the
    /// rule quietly getting weaker. Tab and Shift+Tab move between panes and the status line says
    /// <c>Tab</c> outright when there is more than one; Enter opens what is highlighted, which is
    /// the one gesture a list already means.
    /// </remarks>
    private static readonly string[] Navigation = ["Key.Tab", "Key.Tab.WithShift", "Key.Enter"];

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    [Fact]
    public async Task Every_key_the_surface_binds_is_named_on_the_screen_or_in_the_menu()
    {
        var bound = Bound();

        Assert.NotEmpty(bound);

        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        // Gathered across every pane of every view, because what a region offers depends on where
        // the person is -- and the key that was missing was missing only below the top.
        var offered = new List<string>();

        await foreach (var _ in Walking.EveryPaneAsync(shell))
        {
            offered.Add(shell.Status);
            offered.Add(shell.BackLabel ?? string.Empty);
            offered.AddRange(shell.Actions().Select(action => $"{action.Key}: {action.Label}"));
            offered.Add(Shell.QuitLabel);
        }

        var everything = string.Join(" | ", offered);
        var missing = new List<string>();

        foreach (var key in bound.Where(key => !Navigation.Contains(key, StringComparer.Ordinal)))
        {
            var named = key switch
            {
                "Key.Esc" => everything.Contains(Shell.BackStroke, StringComparison.Ordinal),
                "QuitKey" => everything.Contains(Shell.QuitLabel, StringComparison.Ordinal),
                "BrowserKey" => everything.Contains("F8", StringComparison.Ordinal)
                    || offered.Any(line => line.Contains("browser", StringComparison.Ordinal)),
                _ => false,
            };

            if (!named)
            {
                missing.Add(key);
            }
        }

        Assert.True(
            missing.Count == 0,
            "these keys are answered by the surface and named nowhere a person would look: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void The_actions_key_is_itself_named_wherever_it_is_the_only_way_on()
    {
        // `?` is the key that reveals the others, so it is the one key that cannot rely on the
        // menu to advertise it. The status line has to carry it whenever it carries nothing else.
        Assert.Contains(
            $"{Shell.ActionsKey}:",
            "  " + Shell.ActionsKey + ": actions",
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity: the parse has to find the real bindings. A regex that matched nothing would
        // make the rule above vacuously true, which is the trap the spec suite carries two notes
        // about.
        var bound = Bound();

        Assert.Contains("Key.Tab", bound);
        Assert.Contains("Key.Esc", bound);
        Assert.Contains("QuitKey", bound);
        Assert.Contains("BrowserKey", bound);
    }

    /// <summary>Every key <c>Shell.OnKeyDown</c> compares against, read from its source.</summary>
    /// <returns>The keys, as written.</returns>
    private static IReadOnlyList<string> Bound()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory_Exists(directory))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var source = File.ReadAllText(
            Path.Combine(directory!.FullName, "src", "Parley.Top", "Shell.cs"));

        var from = source.IndexOf("protected override bool OnKeyDown", StringComparison.Ordinal);

        Assert.True(from > 0, "OnKeyDown is not where this test expects it");

        var body = source[from..];
        var ends = body.IndexOf("return base.OnKeyDown", StringComparison.Ordinal);

        Assert.True(ends > 0, "OnKeyDown no longer ends the way this test expects");

        return [.. Regex
            .Matches(
                body[..ends],
                @"key == ([A-Za-z][A-Za-z0-9.]*)",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(5))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)];
    }

    private static bool Directory_Exists(DirectoryInfo directory) =>
        System.IO.Directory.Exists(Path.Combine(directory.FullName, "src"));
}
