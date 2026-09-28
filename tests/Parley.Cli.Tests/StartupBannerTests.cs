using Parley.Configuration;
﻿using System.Globalization;
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
    /// <summary>One project at a chosen endpoint, in a throwaway root.</summary>
    /// <remarks>
    /// `serve` takes projects now: a store is derived from a project's name and its endpoint is its own,
    /// so a rig names a root and writes a project into it rather than passing two paths.
    /// </remarks>
    private static ProjectSettings At(string root, string endpoint, string name = "probe") =>
        new(
            Parley.Core.ProjectName.Create(name),
            ProjectFolder.StoreFor(root, Parley.Core.ProjectName.Create(name)),
            endpoint,
            null);

    [Fact]
    public async Task The_broker_says_its_address_is_not_a_web_page()
    {
        var (output, _) = await ServeUntilListeningAsync();

        // **Worded by the projects path now**, which is the only path there is: the one-store serve
        // said "not a web page" and this says "None of them is a web page", because it is reporting
        // several addresses rather than one.
        Assert.Contains("None of them is a web page", output, StringComparison.Ordinal);

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
        // Non-vacuous, and read off the line the projects path prints: `<project> on <addresses>,
        // store at <path>, schema N`. The one-store path's "Parley broker on" went with the path.
        Assert.Contains("probe on http://127.0.0.1:", output, StringComparison.Ordinal);

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

        // **A directory where the project's `parley.db` goes**, which is the only way to reach this now
        // that no flag names a store. Creating the root alone was not enough — the store path did not
        // exist, SQLite happily created it, and the broker served for ever: the test hung rather than
        // failing, and took the suite with it.
        Directory.CreateDirectory(
            ProjectFolder.StoreFor(directory, Parley.Core.ProjectName.Create("probe")));

        await using var output = new StringWriter();
        await using var error = new StringWriter();

        try
        {
            var code = await ServeCommand.RunAsync(
                [At(directory, "http://127.0.0.1:0")],
                null,
                output,
                error,
                cancellationToken: TestContext.Current.CancellationToken);

            // Non-vacuous: a run that started normally would prove nothing about the failing one.
            Assert.NotEqual(0, code);
            // Named by project: the projects path says `<project>: could not open ...`, where the
            // one-store path had no project to name. That is the more useful sentence on a machine
            // serving several, which is every machine now.
            Assert.Contains("probe: could not open", error.ToString(), StringComparison.Ordinal);
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
            // **Only the version's own parenthesis, not any parenthesis.** The line always ends
            // with the runtime identifier in brackets, so asserting the whole line has none was a
            // check that could not pass in the state it describes — and it only ever runs on a tree
            // with no `.git`, which is why every Windows run took the other branch and this stood.
            // Found by running the suite in a container from `git archive`, which is exactly that
            // tree.
            var version = BuildIdentity.Line[..BuildIdentity.Line.IndexOf(',', StringComparison.Ordinal)];

            Assert.DoesNotContain('(', version);

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

        // Both halves in the one composed block, which ConsoleAnnouncement independently holds
        // to a single write -- the property that keeps it whole. The node hosted beside this
        // writes its own line to the same writer at the same time; ServeCommand wraps that writer
        // in TextWriter.Synchronized, so one call is atomic against it where five would let
        // "Hosting this machine's node" land in the middle of the block. Measured: 200 blocks
        // against a concurrent writer, none split.
        var block = ConsoleAnnouncement(program);

        // **The label and the interpolation, not the variable's name.** This asserted
        // `{consoleAt}` and broke when that local was renamed to say what it holds rather than which
        // removed flag used to set it — a rename that changed no behaviour and no output. It is the
        // third anchor in this file to break on something that was never the point: the other two
        // are recorded in CLAUDE.md, both `var body`. What the test is about is that the address is
        // on its own labelled line and is interpolated rather than written in, and both survive a
        // rename.
        Assert.Contains("Web UI:   {", block, StringComparison.Ordinal);
        Assert.Contains("Open that one in a browser.", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_ways_into_a_deployment_are_named_where_a_person_is_standing()
    {
        // The reader of this block is at a terminal on the machine running the broker, which is
        // frequently not a machine with a browser on it — the case `parley terminal` was built for.
        // Naming only the console here left the terminal surface to be discovered in --help,
        // after deciding the deployment had no way in.
        var block = ConsoleAnnouncement(RepositoryFile.Read("src/Parley.Cli/Program.cs"));

        Assert.Contains("Terminal: parley", block, StringComparison.Ordinal);
        Assert.Contains("no browser needed", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_ways_in_are_pointed_at_the_manifest_being_served()
    {
        // Found by the gate over the installer change, by running `serve --config <file> --with-web-ui` and reading the
        // page: it listed this machine's own projects, because the child located a manifest on its own. The web UI is
        // handed the one being served, and the terminal line names it whenever it is not this machine's default.
        var program = RepositoryFile.Read("src/Parley.Cli/Program.cs");

        Assert.Contains("start.Environment[ProjectManifest.PathVariable] = manifestPath;", program, StringComparison.Ordinal);
        // Bare `parley` when this machine's own manifest is served, which is what a person types; spelled out as
        // `parley terminal --config` when another is, so the arguments reach the terminal tool.
        Assert.Contains("Terminal: parley{(configPath is null ? string.Empty : $\" terminal --config", program, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rule_is_never_shorter_than_what_it_rules()
    {
        // Run rather than read, because this is about what lands on the screen. The rule used to
        // be sized from the address, which was right while the address was the widest thing
        // between the rules; the second route is wider, and a box narrower than its own contents
        // reads as a rendering fault rather than as a deliberately set-apart block.
        //
        // The only test in this file that starts the tool. Every assertion above it is about text
        // the broker composes, and this one is about text the broker measures — which nothing can
        // check without letting it do the measuring.
        var printed = await ConsoleBlockAsync();

        var rules = printed.Where(line => line.Trim().All(c => c == '-')).ToList();
        var content = printed.Where(line => !line.Trim().All(c => c == '-')).ToList();

        // Non-vacuous first: a capture that missed the block would make every assertion below it
        // true of an empty list.
        Assert.Equal(2, rules.Count);
        Assert.Contains(content, line => line.Contains("Terminal: parley", StringComparison.Ordinal));

        var widest = content.Max(line => line.TrimEnd().Length);

        foreach (var rule in rules)
        {
            Assert.True(
                rule.TrimEnd().Length >= widest,
                $"a rule of {rule.TrimEnd().Length} over content {widest} wide:"
                + Environment.NewLine + string.Join(Environment.NewLine, printed));
        }
    }

    /// <summary>Starts a broker with a console and captures the block naming both surfaces.</summary>
    /// <returns>The lines from the opening rule to the closing one, blanks dropped.</returns>
    /// <remarks>
    /// A real port for the console and port zero for the broker. The console's address goes into
    /// the block being measured, so it cannot be zero — the whole subject is how wide that line
    /// renders — while the broker's is never read here and taking a second real port would be a
    /// second chance to collide with something.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> ConsoleBlockAsync()
    {
        // A root with one project in it, on port zero so the operating system picks and the banner
        // reports what it got — which is the thing this file reads.
        var root = Path.Combine(Path.GetTempPath(), "parley-banner-" + Guid.NewGuid().ToString("N"));
        var probe = Parley.Core.ProjectName.Create("probe");

        ProjectManifest.Write(
            root,
            [new ProjectSettings(probe, ProjectFolder.StoreFor(root, probe), "http://127.0.0.1:0", null)]);

        var console = $"http://127.0.0.1:{Loopback.FreePort()}";

        var process = ToolProcess.Launch(
            [],
            "serve",
            "--config", root,
            "--with-web-ui",
            "--web-ui", console);

        var block = new List<string>();

        try
        {
            var reading = Task.Run(() =>
            {
                var lines = new List<string>();

                while (process.StandardOutput.ReadLine() is { } line)
                {
                    if (line.Trim().Length == 0)
                    {
                        continue;
                    }

                    lines.Add(line);

                    // The closing rule, which is the second one. Read to it rather than to a
                    // fixed number of lines: the node and the sweep write to this same stream,
                    // so what sits between the rules is the block and what surrounds it is not
                    // reliably anything.
                    if (lines.Count(l => l.Trim().All(c => c == '-')) == 2)
                    {
                        break;
                    }
                }

                return lines;
            });

            var all = await reading.WaitAsync(
                TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            var opening = all.FindIndex(line => line.Trim().All(c => c == '-') && line.Trim().Length > 0);

            Assert.True(
                opening >= 0,
                "the broker printed no console block: " + string.Join(" / ", all));

            block = all[opening..];
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone, which is the outcome this wanted.
            }

            process.Dispose();

            foreach (var leftover in Directory.Exists(root)
                         ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                         : [])
            {
                try
                {
                    File.Delete(leftover);
                }
                catch (IOException)
                {
                    // A store the broker has not finished releasing.
                }
            }
        }

        return block;
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
        var region = HostingWebUiBlock(RepositoryFile.Read("src/Parley.Cli/Program.cs"));

        var offending = region.Where(character => !char.IsAscii(character)).Distinct().ToList();

        Assert.True(
            offending.Count == 0,
            "the console block is not ASCII: " + string.Join(", ", offending));
    }

    /// <summary>The source of the local function that hosts the console.</summary>
    /// <param name="program">The whole of <c>Program.cs</c>.</param>
    /// <returns>Its text, from the signature to the comment that follows it.</returns>
    private static string HostingWebUiBlock(string program)
    {
        var start = program.IndexOf("async Task<int> HostingWebUi", StringComparison.Ordinal);

        Assert.True(start > 0, "the web UI is no longer started from Program.cs");

        var end = program.IndexOf("// Passed apart", start, StringComparison.Ordinal);

        Assert.True(end > start, "the console block's end marker moved");

        return program[start..end];
    }

    /// <summary>The single write that puts the console's address on the screen.</summary>
    /// <param name="program">The whole of <c>Program.cs</c>.</param>
    /// <returns>The arguments of that call.</returns>
    private static string ConsoleAnnouncement(string program)
    {
        var body = HostingWebUiBlock(program);

        // From where the block is composed, not from the call. It used to start at the call,
        // which worked while every line of the block was a literal argument to it — and the
        // moment the block grew a second route wide enough to need measuring, the text moved
        // into a variable one line above and the extraction found an empty box.
        //
        // The single-write property that start was protecting is now asserted directly, below,
        // rather than implied by everything happening to sit inside one set of parentheses.
        // Anchored at `var opening` rather than `var body`, which is the same lesson this
        // extraction already learned once: the web UI's line moved up one more variable when it
        // gained a second form for the case where the tool did not start. Anchoring at the first
        // step covers however many the composition takes.
        var composed = body.IndexOf("var opening =", StringComparison.Ordinal);

        Assert.True(composed > 0, "the web UI block is no longer composed before it is written");

        var call = body.IndexOf("reporting.WriteLineAsync(", composed, StringComparison.Ordinal);

        Assert.True(call > composed, "the console no longer announces its address");

        // Guarded rather than sliced blind. Without this, a call that stopped ending in
        // ConfigureAwait would fail as an ArgumentOutOfRangeException from a range operator --
        // a stack trace about string indices, for a test whose subject is a banner.
        var ends = body.IndexOf(".ConfigureAwait(false);", call, StringComparison.Ordinal);

        Assert.True(ends > call, "the console announcement no longer ends where this expects");

        var block = body[composed..ends];

        // What the old extraction proved by accident and this proves on purpose. The node hosted
        // beside this writes to the same writer at the same time; ServeCommand wraps it in
        // TextWriter.Synchronized, so one call is atomic against it where several would let
        // "Hosting this machine's node" land in the middle of the block.
        var writes = block.Split("WriteLineAsync(").Length - 1;

        Assert.True(writes == 1, $"the console block is written in {writes} calls, not one");

        return block;
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
                [At(store, "http://127.0.0.1:0")],
                null,
                output,
                error,
                nodeFor: async (_, _, _, _) =>
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
