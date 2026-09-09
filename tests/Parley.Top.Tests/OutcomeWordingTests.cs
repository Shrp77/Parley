using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That the three operator surfaces call an instruction's outcome the same thing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by the review gate as a low finding, and this is the answer to it.</b> Two of the five
/// outcomes need words rather than an enum name: *pending*, which must never read as a failure, and
/// *declined*, which §10.2 requires told apart from one. Each surface carries its own literal —
/// the command line, the terminal's dialog, and three places in the console — and they agree
/// today. Four copies of a phrase agree until one of them is reworded.
/// </para>
/// <para>
/// <b>The alternative was a shared constant and it was not taken.</b> It is the better shape and it
/// belongs beside <c>Standing</c> in <c>Parley.Client</c>, where this repository already keeps
/// vocabulary two surfaces share. Moving it means editing three surfaces to fix a problem that has
/// not happened, at the end of a review gate, where every source change costs the counters. The
/// check is cheaper than the refactor and catches the same failure, so the check is what was
/// written — and whoever does the move will delete this file, which is the right outcome.
/// </para>
/// <para>
/// Read from source rather than from behaviour on purpose. Driving all three surfaces to render an
/// outcome would test that each says <i>something</i>; what matters is that they say the
/// <i>same</i> thing, and that is a fact about the words.
/// </para>
/// </remarks>
public sealed class OutcomeWordingTests
{
    /// <summary>What a turn still running is called, everywhere it is called anything.</summary>
    private const string Running = "still running";

    /// <summary>What a refusal is called, everywhere it is called anything.</summary>
    private const string Declined = "declined, which is its right";

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

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root(), .. parts]));

    /// <summary>Every file that puts an instruction's outcome into words for a person.</summary>
    /// <returns>The sources.</returns>
    private static IEnumerable<(string Where, string Text)> Surfaces()
    {
        yield return ("the command line", Read("src", "Parley.Cli", "Program.cs"));
        yield return ("the terminal", Read("src", "Parley.Top", "Telling.cs"));
        yield return ("the agent's page", Read("src", "Parley.Web", "Pages", "Projects", "Agent.cshtml"));
        yield return ("the console's act page", Read("src", "Parley.Web", "Pages", "Projects", "Act.cshtml.cs"));
        yield return ("the console's composer", Read("src", "Parley.Web", "Pages", "Conversations", "Detail.cshtml.cs"));
    }

    [Fact]
    public void A_running_turn_is_called_the_same_thing_on_every_surface()
    {
        // The one that matters most. An operator who reads three different words for one state on
        // three screens cannot tell whether they are looking at three states.
        var silent = Surfaces()
            .Where(s => !s.Text.Contains(Running, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Where)
            .ToList();

        Assert.True(
            silent.Count == 0,
            $"these do not say '{Running}': " + string.Join(", ", silent));
    }

    [Fact]
    public void A_decline_is_called_the_same_thing_on_every_surface_that_names_it()
    {
        // §10.2 requires a decline told apart from a failure, and §11.5 says why: an agent may
        // refuse because the operator is a person rather than a superior process. The phrase is the
        // whole of how that reaches somebody.
        var naming = Surfaces()
            .Where(s => s.Text.Contains("Declined", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(naming);

        // Compared without case, and the first attempt at this was wrong for want of it: the
        // composer writes "Declined, which is its right" because the phrase starts its sentence,
        // and the others write it lower-case mid-sentence. That is English rather than drift, and a
        // check that called it drift would push somebody into writing a worse sentence to satisfy
        // a test.
        var odd = naming
            .Where(s => !s.Text.Contains(Declined, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Where)
            .ToList();

        Assert.True(
            odd.Count == 0,
            $"these name a decline without saying '{Declined}': " + string.Join(", ", odd));
    }

    [Fact]
    public void No_surface_calls_a_running_turn_a_failure()
    {
        // The specific confusion §10.2 names as making things worse: an operator told a turn failed
        // reissues the instruction. A surface that fell through to "failed" for an unknown outcome
        // would do exactly that, and it is how the defect behaved before this feature.
        foreach (var (where, text) in Surfaces())
        {
            var lines = text.Split('\n');

            var wrong = lines
                .Where(line => line.Contains(Running, StringComparison.Ordinal)
                            && line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(
                wrong.Count == 0,
                $"{where} puts 'failed' on the same line as '{Running}'");
        }
    }
}
