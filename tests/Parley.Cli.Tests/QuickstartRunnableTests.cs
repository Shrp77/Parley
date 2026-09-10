using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That every command the feature's walkthrough tells somebody to type exists.
/// </summary>
/// <remarks>
/// <para>
/// Review gate, lens 3, run 9. <c>quickstart.md</c> is not documentation in the ordinary sense — it is
/// the script T051 runs against a live deployment, on the owner's own machine, with a broker serving
/// real agents. A verb in it that the tool does not accept fails there rather than here, after the
/// step before it has already written files.
/// </para>
/// <para>
/// The tool takes its options centrally, above the verb, so <c>parley --project alpha registry</c> is
/// a real shape and the extraction has to admit it. Fenced blocks only: the walkthrough also describes
/// navigation in prose — <i>parley-top → the beta project → the agent → Withdraw</i> — which is a path
/// through a screen rather than something anybody types.
/// </para>
/// </remarks>
public sealed class QuickstartRunnableTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>The verbs the tool answers to, taken from the help it prints.</summary>
    private static IReadOnlySet<string> Accepted()
    {
        var program = File.ReadAllText(Path.Combine(Root, "src", "Parley.Cli", "Program.cs"));

        return Regex.Matches(program, @"parley ([a-z][a-z-]+)")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every command line inside a fenced block of the walkthrough.</summary>
    private static IReadOnlyList<string> Typed()
    {
        var text = File
            .ReadAllText(Path.Combine(
                Root, "specs", "009-multi-project-enrolment", "quickstart.md"))
            .ReplaceLineEndings("\n");

        var lines = new List<string>();
        var inside = false;

        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inside = !inside;
                continue;
            }

            if (inside && line.TrimStart().StartsWith("parley", StringComparison.Ordinal))
            {
                lines.Add(line.Trim());
            }
        }

        return lines;
    }

    [Fact]
    public void The_walkthrough_names_commands_at_all()
    {
        // The control: every assertion below is over a collection, and an empty one satisfies them.
        Assert.NotEmpty(Typed());
        Assert.True(Accepted().Count >= 6, "the verb extraction found almost nothing");
    }

    [Fact]
    public void Every_command_the_walkthrough_types_is_a_verb_the_tool_accepts()
    {
        var accepted = Accepted();
        var unknown = new List<string>();

        foreach (var line in Typed())
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // `parley-top` is its own tool and takes no verb of its own.
            if (words[0] != "parley")
            {
                continue;
            }

            // Options are taken above the verb, so the verb is the first word that is not one and is
            // not an option's value. `--project alpha registry` is the shape this has to survive.
            var verb = words.Skip(1)
                .Where(word => !word.StartsWith('-'))
                .Where(word => !word.StartsWith('/') && !word.Contains('.', StringComparison.Ordinal))
                .FirstOrDefault(word => accepted.Contains(word));

            if (verb is null)
            {
                unknown.Add(line);
            }
        }

        Assert.True(
            unknown.Count == 0,
            "the walkthrough types a command the tool does not accept:\n" + string.Join("\n", unknown));
    }

    [Fact]
    public void The_walkthrough_does_not_type_a_removed_verb()
    {
        // T051 runs this against a live deployment on the version that removes them, so a line here
        // naming one is a step that fails on somebody's own machine, after the step before it has
        // already written files.
        foreach (var line in Typed())
        {
            Assert.DoesNotContain("parley console", line, StringComparison.Ordinal);
            Assert.DoesNotContain("--with-console", line, StringComparison.Ordinal);
        }
    }
}
