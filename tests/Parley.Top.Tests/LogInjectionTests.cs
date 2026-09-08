using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// What a log line is allowed to do to the screen it is drawn on.
/// </summary>
/// <remarks>
/// <para>
/// A broker's log carries whatever an agent's payload put in it. Round one of this branch's review
/// found that <c>SelectionPrompt</c> renders its choices as markup and that conversation
/// identifiers, agent names and message excerpts were reaching it unescaped — an agent naming a
/// conversation with a tag chose how the operator's menu was drawn. **Choosers were swept then and
/// the log was not**, which is the same omission the round-one finding was about: the escaping went
/// where the hazard had been noticed rather than everywhere it exists.
/// </para>
/// <para>
/// Probed by writing hostile lines into a real file and following it, rather than by reading the
/// call site for a <c>Safe.Text</c>.
/// </para>
/// </remarks>
public sealed class LogInjectionTests : IDisposable
{
    private readonly LogFile _log = new("inject");

    public void Dispose() => _log.Dispose();

    [Theory]
    [InlineData("[red]this is not an error[/]")]
    [InlineData("[link=file:///c:/windows/system32/calc.exe]click[/]")]
    [InlineData("[blink slowblink rapidblink]")]
    [InlineData("agent said: [/]")]
    public async Task A_log_line_cannot_choose_how_the_log_is_drawn(string hostile)
    {
        var token = TestContext.Current.CancellationToken;
        var console = new Collected();
        using var stopping = new CancellationTokenSource();

        var following = LogTail.FollowAsync(console.Write, _log.At, string.Empty, stopping.Token);

        try
        {
            await _log.AppendAsync(hostile);

            // The literal text arrives on screen. If the markup had been interpreted, the tags
            // would have become colour and the brackets would be gone -- and `[/]` on its own is
            // not even well formed, so an unescaped one throws rather than misrenders, taking the
            // log view down over a line somebody else wrote.
            await Eventually.UntilAsync(
                () => console.Output.Contains(hostile, StringComparison.Ordinal),
                $"'{hostile}' did not reach the screen as written",
                token);
        }
        finally
        {
            await stopping.CancelAsync();
            await following;
        }

        Assert.True(following.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_filter_is_matched_against_the_line_and_not_against_its_markup()
    {
        // The filter is what somebody typed, and the line is what an agent wrote. Neither may
        // become markup, and the match has to be on the text rather than on whatever escaping
        // turned it into -- a filter that missed a line because the escaping moved the characters
        // would hide exactly the lines somebody was searching for.
        var token = TestContext.Current.CancellationToken;
        var console = new Collected();
        using var stopping = new CancellationTokenSource();

        var following = LogTail.FollowAsync(console.Write, _log.At, "[warn]", stopping.Token);

        try
        {
            await _log.AppendAsync("a line with [warn] in it");
            await _log.AppendAsync("a line without");

            await Eventually.UntilAsync(
                () => console.Output.Contains("[warn] in it", StringComparison.Ordinal),
                "the filter did not match a line containing what was typed",
                token);

            Assert.DoesNotContain("a line without", console.Output, StringComparison.Ordinal);
        }
        finally
        {
            await stopping.CancelAsync();
            await following;
        }
    }
}
