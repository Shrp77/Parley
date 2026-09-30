using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That every action the console will perform is reachable from a page, and every action a page offers
/// is one the console performs.
/// </summary>
/// <remarks>
/// <para>
/// <b>020's quickstart walk found three actions bound in <c>Act</c>'s handler and linked from nowhere.</b>
/// Asking an agent what it owns, asking every agent, and recording what a person accepted were all
/// implemented, all tested, and all unreachable: the agent page offered <em>Instruct</em>, <em>Retire</em>
/// and <em>Purge</em> and nothing else, and <c>Act</c>'s <c>OnGet</c> answered the acceptance with "this is
/// not something the console does" while its <c>OnPostAsync</c> would have done it.
/// </para>
/// <para>
/// <b><c>DriftTests</c> could not see it and is right not to.</b> It compares the operations
/// <c>BrokerClient</c> offers against the two surface trees, and <c>RecordOwnershipAsync</c> genuinely
/// appears in this tree — in the page model. What it cannot know is whether anything a person touches
/// leads there. This is the same shape as <c>Shell.Said</c> being written in twenty places and read by
/// nothing, and as the line pane with no size: <b>a property the suite holds to and the screen never
/// shows.</b>
/// </para>
/// <para>
/// Both directions, because the other one has its own failure. An <c>action=</c> link naming something the
/// handler does not perform is a page that reports "this is not something the console does" to whoever
/// followed it, which reads as a broken console rather than as a link nobody finished.
/// </para>
/// </remarks>
public sealed class EveryActionHasADoorTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "specs")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static string Pages() => Path.Combine(Root(), "src", "Parley.Web", "Pages");

    private static string Model() =>
        File.ReadAllText(Path.Combine(Pages(), "Projects", "Act.cshtml.cs"));

    private static string Markup() =>
        string.Join(
            '\n',
            Directory
                .EnumerateFiles(Pages(), "*.cshtml", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

    /// <summary>
    /// The lowercase action names <c>Act</c> will perform, taken from the arms of its two switches.
    /// </summary>
    /// <remarks>
    /// Read out of the source rather than listed here, because a list is what went stale: the handler
    /// grew three arms and whatever named them all would have had to be remembered.
    /// </remarks>
    private static IEnumerable<string> Performed() =>
        System.Text.RegularExpressions.Regex
            .Matches(Model(), """^\s*"(?<action>[a-z]+)" =>""",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups["action"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    private static IEnumerable<string> Offered() =>
        System.Text.RegularExpressions.Regex
            .Matches(Markup(), "action=(?<action>[a-z]+)")
            .Select(match => match.Groups["action"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    [Fact]
    public void The_actions_read_out_of_the_handler_are_not_an_empty_set()
    {
        // The guard above this one passes by vacuity if the extraction matches nothing, which is the
        // trap `The_document_contains_a_schema` exists for in the spec suite. Named actions this
        // console has had for several releases, so a rename that breaks the extraction fails here
        // rather than making the real check silently hold.
        var performed = Performed().ToList();

        Assert.Contains("retire", performed);
        Assert.Contains("purge", performed);
        Assert.Contains("instruct", performed);
        Assert.True(performed.Count >= 6, string.Join(", ", performed));
    }

    [Fact]
    public void Every_action_the_console_performs_is_reachable_from_a_page()
    {
        // Must fail against 0.34.0, where `askownership`, `askeveryownership` and `ownership` were
        // performed by the handler and linked from no page at all.
        var offered = Offered().ToHashSet(StringComparer.Ordinal);

        var unreachable = Performed().Where(action => !offered.Contains(action)).ToList();

        Assert.True(
            unreachable.Count == 0,
            "Act performs these and no page links to them: " + string.Join(", ", unreachable));
    }

    [Fact]
    public void Every_action_a_page_offers_is_one_the_console_performs()
    {
        var performed = Performed().ToHashSet(StringComparer.Ordinal);

        var dangling = Offered().Where(action => !performed.Contains(action)).ToList();

        Assert.True(
            dangling.Count == 0,
            "a page links to these and Act performs none of them: " + string.Join(", ", dangling));
    }

    [Fact]
    public void Every_action_says_what_it_would_do_before_a_post_does_it()
    {
        // `OnGet` describes and `OnPostAsync` acts, so an action with an arm in the second and none in
        // the first reaches a person as "this is not something the console does" above a button that
        // does it anyway. That is what the acceptance did at 0.34.0.
        var model = Model();

        // `OnGetAsync` since 022, because accepting an agent's answer shows the answer and that is a read.
        var get = model[model.IndexOf("public async Task<IActionResult> OnGetAsync()", StringComparison.Ordinal)..
                        model.IndexOf("public async Task<IActionResult> OnPostAsync()", StringComparison.Ordinal)];

        var post = model[model.IndexOf("public async Task<IActionResult> OnPostAsync()", StringComparison.Ordinal)..];

        static IEnumerable<string> Arms(string text) =>
            System.Text.RegularExpressions.Regex
                .Matches(text, """^\s*"(?<action>[a-z]+)" =>""",
                    System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(match => match.Groups["action"].Value)
                .Distinct(StringComparer.Ordinal);

        var described = Arms(get).ToHashSet(StringComparer.Ordinal);
        var silent = Arms(post).Where(action => !described.Contains(action)).ToList();

        Assert.True(
            silent.Count == 0,
            "these are done on a POST and described on no GET: " + string.Join(", ", silent));
    }
}
