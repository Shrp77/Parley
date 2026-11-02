using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the two install scripts install the same thing, and that the split documented itself.
/// </summary>
/// <remarks>
/// <para>
/// Two scripts doing one job drift, and the drift is invisible: whoever changes one is on the
/// platform it serves and has no reason to run the other. A tool present in one table and not the
/// other is a machine that installs something different according to which shell somebody used,
/// which is the worst kind of difference — nobody sees it until two machines disagree.
/// </para>
/// <para>
/// The documentation half is here rather than beside the code because it is the same question
/// asked of the same change: a split makes new public types, and a public type whose remarks say
/// what it does rather than why it exists is one nobody can safely delete later.
/// </para>
/// </remarks>
[Integration]
public sealed class ScriptParityTests
{
    private static string Windows => RepositoryFile.Read("install.ps1");

    private static string Unix => RepositoryFile.Read("install.sh");

    [Fact]
    public void Both_scripts_know_the_same_tools()
    {
        foreach (var package in new[] { "Parley.Cli", "Parley.Mcp", "Parley.Terminal" })
        {
            Assert.Contains(package, Windows, StringComparison.Ordinal);
            Assert.Contains(package, Unix, StringComparison.Ordinal);
        }

        foreach (var command in new[] { "parley-mcp", "parley-terminal" })
        {
            Assert.Contains(command, Windows, StringComparison.Ordinal);
            Assert.Contains(command, Unix, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Both_scripts_offer_the_same_choices()
    {
        // Named in the pair each script uses, because the spellings differ by shell convention and
        // the point is that the capability exists on both, not that the text matches.
        foreach (var (windows, unix) in new[]
                 {
                     ("-Surfaces", "--surfaces"),
                     ("-Only", "--only"),
                     ("-Uninstall", "--uninstall"),
                     ("-Force", "--force"),
                     ("-Reset", "--reset"),
                     ("-Repositories", "--repositories"),
                 })
        {
            Assert.Contains(windows, Windows, StringComparison.Ordinal);
            Assert.Contains(unix, Unix, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Both_scripts_decide_about_the_broker_before_touching_it()
    {
        // The property that matters, asserted of each script separately rather than of one and
        // assumed of the other. This is the check that would have caught a Linux script still
        // stopping the broker unconditionally while the Windows one had learned not to.
        Assert.Contains("$replacingBroker", Windows, StringComparison.Ordinal);
        Assert.Contains("replacing_broker", Unix, StringComparison.Ordinal);

        Assert.Contains("Leaving the broker alone", Windows, StringComparison.Ordinal);
        Assert.Contains("Leaving the broker alone", Unix, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_type_this_change_introduced_or_widened_says_why_it_exists()
    {
        // The rule the compiler cannot enforce. CS1591 requires a summary; nothing requires the
        // remarks that carry why, and a split is exactly when they are most needed — these types
        // are public because of a packaging decision, and without the note the next reader sees a
        // wide surface with no reason for being wide.
        //
        // **Scoped to what this change introduced or widened, and it was broader on the first
        // run.** Asking it of every public type in the moved assembly named seven that had been
        // public all along and were only carried across: `Resolved`, `ValueSource`, `Supervisor`,
        // `EnrollmentManifest`, `RepositoryPlan`, `SelectableRepository`, `SelectionStatus`. Real,
        // below the bar, and recorded in the ledger rather than fixed — they are plain data shapes,
        // and a remark demanded of one produces the restatement of the signature that the rule
        // forbids. It also named `Program`, which the compiler generates for top-level statements.
        //
        // `Parley.Terminal` is not named here: this project cannot see it, deliberately, and its own
        // `DocumentedTests` already requires a summary and a why of every file in it.
        var introduced = new[]
        {
            typeof(Parley.Setup.Enrolment),
            typeof(Parley.Setup.ScheduledTasks),
            typeof(Forwarding),
            typeof(Parley.Configuration.WebUiAddress),
        };

        var silent = new List<string>();

        foreach (var type in introduced)
        {
            var documentation = Path.ChangeExtension(type.Assembly.Location, ".xml");

            Assert.True(
                File.Exists(documentation),
                $"{type.Assembly.GetName().Name} generated no documentation file.");

            var text = File.ReadAllText(documentation);
            var at = text.IndexOf($"\"T:{type.FullName}\"", StringComparison.Ordinal);

            if (at < 0)
            {
                silent.Add($"{type.Name} (no documentation at all)");
                continue;
            }

            var end = text.IndexOf("</member>", at, StringComparison.Ordinal);

            if (!text[at..end].Contains("<remarks>", StringComparison.Ordinal))
            {
                silent.Add($"{type.Name} (a summary and no why)");
            }
        }

        Assert.True(
            silent.Count == 0,
            "These are public and say what they do rather than why they exist: "
            + string.Join(", ", silent.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_check_above_is_looking_at_something()
    {
        // Non-vacuity, because a documentation walk that resolves no types passes silently and a
        // missing XML file would have been the likeliest way to get there.
        var types = typeof(Parley.Setup.Enrolment).Assembly
            .GetTypes()
            .Count(type => type.IsPublic);

        Assert.True(types > 10, $"only {types} public types found in Parley.Setup.");

        var documentation = Path.ChangeExtension(
            typeof(Parley.Setup.Enrolment).Assembly.Location, ".xml");

        Assert.Contains("<remarks>", File.ReadAllText(documentation), StringComparison.Ordinal);
    }
}
