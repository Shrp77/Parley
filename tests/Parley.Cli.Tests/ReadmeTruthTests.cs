using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the README does not promise a spelling this version refuses.
/// </summary>
/// <remarks>
/// <para>
/// Review gate, lens 3, run 5. The README said <i>"`--with-console`, `--console` and `--no-console`
/// are the old spellings. They still work and are removed at 0.21.0"</i> — wrong twice over in the
/// release that removes them: 008 moved the date to 0.22.0 and recorded why, and this is 0.22.0, so
/// they do not still work.
/// </para>
/// <para>
/// <b>It is the README that makes this worth a check rather than a correction.</b> It is the one
/// document that has to stand alone — somebody reading it has not read SPEC.md, the ledger, or 008's
/// record — so a sentence in it that describes the previous version is not stale prose, it is an
/// instruction that fails.
/// </para>
/// <para>
/// Deliberately narrow. It asks whether the README claims a removed spelling <i>works</i>, not whether
/// it mentions one: naming what was removed is exactly what a deprecation note is for, and a check
/// that forbade the words would delete the paragraph a person needs.
/// </para>
/// </remarks>
public sealed class ReadmeTruthTests
{
    private static readonly string Readme = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "README.md"));

    private static readonly string[] Removed = ["--with-console", "--console", "--no-console"];

    [Fact]
    public void The_readme_does_not_say_a_removed_spelling_still_works()
    {
        var text = File.ReadAllText(Readme).ReplaceLineEndings("\n");

        // The control: without it a moved or renamed README passes this silently.
        Assert.Contains("--no-web-ui", text, StringComparison.Ordinal);

        foreach (var paragraph in text.Split("\n\n"))
        {
            if (!Removed.Any(flag => paragraph.Contains(flag, StringComparison.Ordinal)))
            {
                continue;
            }

            Assert.False(
                paragraph.Contains("still work", StringComparison.OrdinalIgnoreCase),
                "the README says a removed spelling still works:\n" + paragraph);
        }
    }
}
