using System.Globalization;
using System.Runtime.InteropServices;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the lines a broker prints on startup send a person to the right address.
/// </summary>
/// <remarks>
/// <para>
/// Written because somebody browsed to the broker. Its address is the first URL on the screen and
/// looks like the one to open; what a browser gets there is "an HTTP/1.x request was sent to an
/// HTTP/2 only endpoint", which is an error about the wrong thing and reads as the deployment
/// being broken rather than as the wrong address.
/// </para>
/// <para>
/// The banner is tee'd into <c>--log</c>, so it is plain text on purpose. Colour would put escape
/// codes into a file somebody greps, and a supervisor's log is read long after the terminal that
/// might have rendered them is gone.
/// </para>
/// </remarks>
public sealed class StartupBannerTests
{
    [Fact]
    public async Task The_broker_says_its_address_is_not_a_web_page()
    {
        var (output, _) = await ServeUntilListeningAsync();

        Assert.Contains("not a web page", output, StringComparison.Ordinal);

        // And still says the thing every other reader depends on.
        Assert.Contains("Ctrl+C stops", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_in_the_banner_is_an_escape_code()
    {
        // The reason it is ruled rather than coloured. This writer is copied into --log, and an
        // operator reading a machine that started nothing should not be reading terminal escapes.
        var (output, error) = await ServeUntilListeningAsync();

        // Non-vacuous first. A harness that captured nothing would pass both
        // assertions below it and report a banner nobody had checked.
        Assert.Contains("Parley broker on", output, StringComparison.Ordinal);

        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('\u001b', error);
    }

    [Fact]
    public async Task The_first_thing_a_broker_says_is_which_versions_are_running()
    {
        var (output, _) = await ServeUntilListeningAsync();

        var first = output.Split('\n')[0].TrimEnd('\r');

        // First, because the runs where this matters most are the ones that print nothing after
        // it: a store this build cannot open, or a port already held.
        Assert.StartsWith("parley ", first, StringComparison.Ordinal);

        // Every version involved, each answering a different question. Named individually rather
        // than matched as one string, so a line that drops one says which.
        Assert.Contains(BuildIdentity.Version, first, StringComparison.Ordinal);
        Assert.Contains("protocol " + ProtocolVersion.Current, first, StringComparison.Ordinal);
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"schema {SchemaMigrator.LatestVersion}"),
            first,
            StringComparison.Ordinal);
        Assert.Contains(RuntimeInformation.FrameworkDescription, first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_that_cannot_open_its_store_has_still_named_its_versions()
    {
        // The run this line exists for. Everything else the banner says comes after the store is
        // open, so a build that refuses a store — "this store is at schema 5 and this Parley only
        // knows 4" — used to leave a log with the complaint in it and nothing saying which Parley.
        //
        // A directory rather than a locked or malformed file, because it fails the same way on
        // every platform this suite runs on and needs no cleanup of its own.
        var directory = Path.Combine(
            Path.GetTempPath(), "parley-banner-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        await using var output = new StringWriter();
        await using var error = new StringWriter();

        try
        {
            var code = await ServeCommand.RunAsync(
                directory,
                "http://127.0.0.1:0",
                null,
                output,
                error,
                cancellationToken: TestContext.Current.CancellationToken);

            // Non-vacuous: a run that started normally would prove nothing about the failing one.
            Assert.NotEqual(0, code);
            Assert.Contains("Could not open", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Contains(BuildIdentity.Line, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_version_line_names_the_commit_when_the_build_carried_one()
    {
        // Not asserted as present: a build made outside source control carries no revision, and a
        // test demanding one would fail on a checkout that is a copy rather than a clone. What is
        // asserted is that it is never invented, and never the whole forty characters — the line
        // is read on a terminal, and a hash that wraps it is the reason `git log` abbreviates.
        if (BuildIdentity.Commit is null)
        {
            Assert.DoesNotContain('(', BuildIdentity.Line);

            return;
        }

        Assert.Contains(
            "(" + BuildIdentity.Commit + ")", BuildIdentity.Line, StringComparison.Ordinal);

        Assert.True(
            BuildIdentity.Commit.Length <= 7,
            $"the commit is {BuildIdentity.Commit.Length} characters: {BuildIdentity.Commit}");
    }

    [Fact]
    public void What_version_prints_and_what_the_broker_logs_agree_about_the_number()
    {
        // They are deliberately not the same line — one is a number somebody compares, the other
        // identifies a build months later — and that is exactly why the number they share is
        // checked. Two readings of the same attribute is how they came to differ once.
        var program = RepositoryFile.Read("src/Parley.Cli/Program.cs");

        Assert.Contains("BuildIdentity.Version", program, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "AssemblyInformationalVersionAttribute", program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_console_address_is_set_apart_from_everything_else()
    {
        // It used to be the third line of five, in the same weight as the broker's address and
        // the node's catalogue. A person scanning for somewhere to click found the first URL.
        //
        // Asserted against the source because the announcement is in top-level statements with no
        // seam to call; the rendering itself was checked by running it.
        var program = RepositoryFile.Read("src/Parley.Cli/Program.cs");

        // Both halves inside ONE call's arguments, and that is the point rather than a
        // convenience: ConsoleAnnouncement stops at the first ConfigureAwait, so finding the
        // address and the instruction in it proves the block is written as a single write.
        //
        // Which is what keeps it whole. The node hosted beside this writes its own line to the
        // same writer at the same time; ServeCommand wraps that writer in TextWriter.Synchronized,
        // so one call is atomic against it where five would let "Hosting this machine's node"
        // land in the middle of the block. Measured: 200 blocks against a concurrent writer, none
        // split.
        var block = ConsoleAnnouncement(program);

        Assert.Contains("Console:  {consoleAt}", block, StringComparison.Ordinal);
        Assert.Contains("Open that one in a browser.", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_the_console_block_prints_needs_a_code_page()
    {
        // Ruled with ASCII rather than box-drawing, and this replaced an assertion that pinned
        // `new string('-'` -- which tested how the rule was built rather than what it has to be,
        // and would have failed on a rewrite that was in every way correct. AutostartConsoleTests
        // records the same lesson from the other direction: an assertion that breaks on an
        // innocent change teaches the next person to loosen it rather than to keep the guard.
        //
        // The whole region, comments included, because this process's own warnings already lose
        // their section signs on a console whose code page nobody chose. A banner that renders as
        // question marks is worse than a plain one.
        var region = HostingConsole(RepositoryFile.Read("src/Parley.Cli/Program.cs"));

        var offending = region.Where(character => !char.IsAscii(character)).Distinct().ToList();

        Assert.True(
            offending.Count == 0,
            "the console block is not ASCII: " + string.Join(", ", offending));
    }

    /// <summary>The source of the local function that hosts the console.</summary>
    /// <param name="program">The whole of <c>Program.cs</c>.</param>
    /// <returns>Its text, from the signature to the comment that follows it.</returns>
    private static string HostingConsole(string program)
    {
        var start = program.IndexOf("async Task<int> HostingConsole", StringComparison.Ordinal);

        Assert.True(start > 0, "the console is no longer hosted from Program.cs");

        var end = program.IndexOf("// Passed apart", start, StringComparison.Ordinal);

        Assert.True(end > start, "the console block's end marker moved");

        return program[start..end];
    }

    /// <summary>The single write that puts the console's address on the screen.</summary>
    /// <param name="program">The whole of <c>Program.cs</c>.</param>
    /// <returns>The arguments of that call.</returns>
    private static string ConsoleAnnouncement(string program)
    {
        var body = HostingConsole(program);
        var call = body.IndexOf("reporting.WriteLineAsync(", StringComparison.Ordinal);

        Assert.True(call > 0, "the console no longer announces its address");

        // Guarded rather than sliced blind. Without this, a call that stopped ending in
        // ConfigureAwait would fail as an ArgumentOutOfRangeException from a range operator --
        // a stack trace about string indices, for a test whose subject is a banner.
        var ends = body.IndexOf(".ConfigureAwait(false);", call, StringComparison.Ordinal);

        Assert.True(ends > call, "the console announcement no longer ends where this expects");

        return body[call..ends];
    }


    /// <summary>Runs a broker just long enough to print its banner, then stops it.</summary>
    /// <returns>What it wrote to output and to error.</returns>
    /// <remarks>
    /// Stopped from the hook rather than after a delay: the hook runs once the port is open,
    /// which is exactly after the banner and before anything waits. A timed test here would be
    /// slow when it passed and flaky when the machine was busy.
    /// </remarks>
    private static async Task<(string Output, string Error)> ServeUntilListeningAsync()
    {
        var store = Path.Combine(
            Path.GetTempPath(), "parley-banner-" + Guid.NewGuid().ToString("N") + ".db");

        using var stopping = new CancellationTokenSource();
        await using var output = new StringWriter();
        await using var error = new StringWriter();

        try
        {
            await ServeCommand.RunAsync(
                store,
                "http://127.0.0.1:0",
                null,
                output,
                error,
                whenListening: async (_, _, _) =>
                {
                    await stopping.CancelAsync().ConfigureAwait(false);

                    return 0;
                },
                cancellationToken: stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // The interrupt this test causes on purpose.
        }
        finally
        {
            foreach (var leftover in Directory.GetFiles(
                Path.GetDirectoryName(store)!, Path.GetFileName(store) + "*"))
            {
                try
                {
                    File.Delete(leftover);
                }
                catch (IOException)
                {
                    // A store the broker has not finished releasing. Temporary, and not this
                    // test's business.
                }
            }
        }

        return (output.ToString(), error.ToString());
    }
}
