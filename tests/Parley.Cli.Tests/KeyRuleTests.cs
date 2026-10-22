using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the launch key has one implementation, checked by reading the sources.
/// </summary>
/// <remarks>
/// <para>
/// The "is this entry ours" decision is one implementation rather than six:
/// <see cref="LaunchKey"/>, and until something checks it that is an intention rather than a property
/// — the same distinction <c>GraphTests</c> and <c>SurfaceTests</c> exist to make for their own rules.
/// </para>
/// <para>
/// <b>Read from the sources rather than from behaviour, because the failure is a second copy that
/// happens to agree today.</b> A duplicated rule passes every behavioural test the day it is written;
/// what it costs is paid later, by whoever edits one of them. This repository has recorded that shape
/// three times — in <c>WorkspacePath</c>, in <c>ProjectName</c>, and in the two spellings of the
/// deprecated console flag.
/// </para>
/// </remarks>
public sealed class KeyRuleTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>Every C# source file that ships, with its path.</summary>
    private static IEnumerable<(string Path, string Text)> Sources()
    {
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            yield return (file, File.ReadAllText(file));
        }
    }

    /// <summary>The lines of a file that are code rather than comment.</summary>
    /// <remarks>
    /// Comments are where this feature's reasoning lives and several of them quote the key on purpose —
    /// the adapter's own header shows an example <c>.mcp.json</c>, which is required. A check that
    /// could not tell those apart would either fail on documentation or have to be switched off.
    /// </remarks>
    private static IEnumerable<(int Number, string Text)> CodeLines(string text)
    {
        var number = 0;

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            number++;

            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("///", StringComparison.Ordinal)
                || trimmed.StartsWith('*'))
            {
                continue;
            }

            yield return (number, line);
        }
    }

    [Fact]
    public void The_key_is_spelled_in_exactly_one_file()
    {
        // **Two other things wear this word and neither is a key.** `AutostartPlan` names the
        // *executable* — what to run, not what a server sits under — and `Shell` sets the terminal's
        // window *title*. Both are allowed by name rather than by pattern, so that a fourth meaning is
        // a decision somebody makes in this list rather than a check that quietly stops applying.
        var offenders = new List<string>();

        foreach (var (path, text) in Sources())
        {
            if (path.EndsWith("LaunchKey.cs", StringComparison.Ordinal)
                || path.EndsWith("AutostartPlan.cs", StringComparison.Ordinal)
                || path.EndsWith("Shell.cs", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (number, line) in CodeLines(text))
            {
                if (line.Contains("\"parley\"", StringComparison.Ordinal)
                    || line.Contains("\"parley-\"", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(Root, path)}:{number}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "the launch key is spelled outside LaunchKey: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Nothing_else_derives_a_key_from_a_project_name()
    {
        // The other half, and the one a second implementation actually looks like: not the literal,
        // but the concatenation. `"parley-" + project` written anywhere else is a key rule that will
        // not learn about a change to this one.
        var offenders = new List<string>();

        foreach (var (path, text) in Sources())
        {
            if (path.EndsWith("LaunchKey.cs", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (number, line) in CodeLines(text))
            {
                // **Qualified, because `Prefix +` also matches `FocusPrefix +`.** An unqualified
                // match would report the terminal's status line as a second key rule, which is
                // the shape a source-scanning check fails in: a substring that is a word somewhere
                // else. It names `LaunchKey.Prefix`, which is what a second implementation would
                // actually have to write.
                if (line.Contains("LaunchKey.Prefix +", StringComparison.Ordinal)
                    || line.Contains("+ LaunchKey.Prefix", StringComparison.Ordinal)
                    || line.Contains("$\"parley-{", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(Root, path)}:{number}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "a key is composed outside LaunchKey: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_reader_of_a_launch_configuration_goes_through_one_reader()
    {
        // Every path that reads a repository's enrolment finds entries under both
        // spellings. That is achievable by discipline or by construction, and this is which: the
        // `mcpServers` object is only ever handed to `LaunchEntries.Read`.
        // **Matched across the whole file rather than line by line, because the calls span lines.**
        // A line-by-line check would report two correct call sites: `LaunchEntries.Read(` on
        // one line and its `["mcpServers"]` argument on the next. A rule about code that is written
        // to fit in a hundred columns cannot be enforced by a check that assumes it is not.
        var offenders = new List<string>();

        foreach (var (path, text) in Sources())
        {
            if (path.EndsWith("LaunchEntries.cs", StringComparison.Ordinal)
                || path.EndsWith("EnrollmentPlan.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var code = string.Join(" ", CodeLines(text).Select(line => line.Text.Trim()));

            var at = code.IndexOf("[\"mcpServers\"]", StringComparison.Ordinal);

            while (at >= 0)
            {
                // The reader's name has to appear near enough to be the same expression. A window
                // rather than a parse, and the window is stated rather than tuned: a call and its
                // argument sit inside two source lines of each other, and two lines is about this.
                var from = Math.Max(0, at - 160);
                var near = code[from..Math.Min(code.Length, at + 160)];

                // The *type* rather than `LaunchEntries.Read`, because joining lines puts a space
                // between the two halves of a member access that was wrapped — which is how this
                // check reported its fourth false positive. Naming the type is enough: nothing else
                // is called `LaunchEntries`, and a reader that did not use it would not mention it.
                if (!near.Contains("LaunchEntries", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(Root, path)} near '{near.Trim()[..Math.Min(60, near.Trim().Length)]}'");
                }

                at = code.IndexOf("[\"mcpServers\"]", at + 1, StringComparison.Ordinal);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "a launch configuration is read without LaunchEntries: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_check_can_actually_see_the_sources()
    {
        // **The control, and without it every assertion above passes on an empty enumeration.** This
        // walks up from the test binary to the repository root, which is exactly the kind of path
        // that stops resolving the first time an output layout changes — and would do it silently.
        var sources = Sources().ToArray();

        Assert.True(sources.Length > 50, $"only {sources.Length} sources found; the walk is wrong");

        Assert.Contains(
            sources,
            source => source.Path.EndsWith("LaunchKey.cs", StringComparison.Ordinal));
    }
}
