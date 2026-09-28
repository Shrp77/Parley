using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What the install scripts promise, checked against what the repository actually contains.
/// </summary>
/// <remarks>
/// <para>
/// A shell script is the part of a project least likely to be run by whoever changes the thing
/// it installs. Renaming a project, adding a third tool or moving an output directory all leave
/// these two files quietly wrong — and the failure lands on somebody cloning the repository for
/// the first time, which is the worst audience for it.
/// </para>
/// <para>
/// So this checks the claims that can be checked from here: that both scripts exist, that they
/// name every packable tool and no others, that neither hard-codes a version, and that both do
/// the two things they exist to do. What they cannot check is that the scripts run, which is why
/// they were run by hand on both shells before being committed.
/// </para>
/// </remarks>
public sealed class InstallScriptTests
{
    private static string Windows => RepositoryFile.Read("install.ps1");

    private static string Unix => RepositoryFile.Read("install.sh");

    public static TheoryData<string> BothScripts => ["install.ps1", "install.sh"];

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_installs_every_tool_this_repository_ships(string script)
    {
        // Both packable projects, by name. A third tool added without touching these would
        // install two of three and report success, and the person who noticed would be a new
        // user whose launch configuration named something that was not there.
        var text = RepositoryFile.Read(script);

        Assert.Contains("Parley.Cli", text, StringComparison.Ordinal);
        Assert.Contains("Parley.Mcp", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void No_script_hard_codes_a_version(string script)
    {
        // The version comes from what was packed, not from a literal. A literal here would be
        // correct on the day it was written and would then install something else for ever,
        // silently, because the install would still succeed.
        var text = RepositoryFile.Read(script);

        Assert.DoesNotMatch(@"--version\s+[""']?\d+\.\d+\.\d+", text);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_clears_stale_packages_before_packing(string script)
    {
        // The first of the two traps. `--add-source` treats a directory as a feed, and
        // bin/Release accumulates every version ever packed there -- so an install from a
        // "clean" clone can serve a package from weeks ago and report success.
        var text = RepositoryFile.Read(script);

        Assert.Contains("nupkg", text, StringComparison.Ordinal);

        var clears = text.IndexOf("nupkg", StringComparison.Ordinal);
        var packs = text.IndexOf("dotnet pack", StringComparison.Ordinal);

        Assert.True(
            clears < packs,
            $"{script} packs before it clears stale packages, which is the wrong order: the " +
            "package it just built would be deleted or the old ones would still be there.");
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_uninstalls_before_installing(string script)
    {
        // The second trap, and the reason `update` is not used. `dotnet tool update` does
        // nothing at all when the version has not changed -- it reports success at some length
        // and replaces nothing -- so a rebuilt tool appears to install and does not.
        var text = RepositoryFile.Read(script);

        Assert.Contains("tool uninstall", text, StringComparison.Ordinal);
        Assert.Contains("tool install", text, StringComparison.Ordinal);

        // Only what runs. Both scripts name `dotnet tool update` in a comment explaining why
        // they do not use it, and that explanation is the most useful thing in either file --
        // a check that forbade the words would delete the reasoning to satisfy itself.
        Assert.DoesNotContain(
            Executable(text), line => line.Contains("tool update", StringComparison.Ordinal));
    }

    /// <summary>The executable lines of a script, as one string.</summary>
    /// <param name="text">The script.</param>
    /// <returns>Its code, with every comment removed.</returns>
    /// <remarks>
    /// Ordering checks need this rather than the raw file, for the same reason
    /// <see cref="Executable"/> exists at all: these scripts explain themselves at length, so the
    /// first mention of any command is usually the paragraph saying why it is used. A check
    /// reading positions out of the raw text compares a comment against a line of code and fails
    /// a script that is correct — which it did, against the note explaining this.
    /// </remarks>
    private static string Code(string text) => string.Join('\n', Executable(text));

    /// <summary>The lines of a script that are not comments.</summary>
    /// <param name="text">The script.</param>
    /// <returns>Its executable lines.</returns>
    /// <remarks>
    /// PowerShell's block comment is why this is a method rather than a filter written inline.
    /// A <c>&lt;# ... #&gt;</c> header spans many lines and none of them begins with a hash, so a
    /// line-by-line filter reads the whole explanation at the top of install.ps1 as code — which
    /// it did, and the check failed against the paragraph explaining what the check is for.
    /// </remarks>
    private static string[] Executable(string text) =>
        [.. System.Text.RegularExpressions.Regex
            .Replace(text, @"<#.*?#>", string.Empty,
                System.Text.RegularExpressions.RegexOptions.Singleline)
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'))];

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_checks_what_ended_up_on_the_path(string script)
    {
        // The check the scripts exist for. Every other step can report success while leaving an
        // older tool in place, so the only honest confirmation is asking the thing on PATH what
        // version it is and comparing it with what was built.
        var text = RepositoryFile.Read(script);

        Assert.Contains("--version", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_can_remove_what_it_installed(string script)
    {
        // Somebody testing a fresh install needs to get back to nothing, and doing that by hand
        // means knowing that a tool leaves a shim and a store entry behind. It also has to leave
        // the deployment alone: a person reinstalling a tool has not asked to lose their
        // database or their launch configurations.
        var text = RepositoryFile.Read(script);

        Assert.Contains("ninstall", text, StringComparison.Ordinal);
        Assert.Contains(".mcp.json", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_refuses_before_it_stops_anything_when_an_adapter_holds_the_tools(
        string script)
    {
        // Observed: a running parley-mcp holds its own files in the tool store, so the uninstall
        // fails with a denied path. The error was clear; the order was not. The broker had
        // already been stopped, so a machine that had been serving a deployment was left with no
        // broker, one tool replaced and one not, by a script that had reported only success.
        //
        // Checked as ordering rather than as presence, because a check that ran after the stop
        // would read exactly the same in a diff and fix nothing.
        var text = RepositoryFile.Read(script);

        // The message the guard prints, not the name of the tool: `parley-mcp` appears in the
        // table of tools near the top of both scripts, so anchoring on it would pass with the
        // guard deleted -- a check that asserts the tool list comes before the install step.
        var guard = text.IndexOf(
            "hold the files this would replace", StringComparison.Ordinal);
        var stop = script.EndsWith(".ps1", StringComparison.Ordinal)
            ? text.IndexOf("Stopping any running broker", StringComparison.Ordinal)
            // The shell script names its stop with a function, and the first mention of that
            // is the definition. "Installing version" is the point of no return in both.
            : text.IndexOf("Installing version", StringComparison.Ordinal);

        Assert.True(guard >= 0, $"{script} does not look for a running adapter at all");
        Assert.True(stop >= 0, $"{script} no longer stops the broker where this test expects");
        Assert.True(
            guard < stop,
            $"{script} checks for a running adapter after stopping the broker, which is the "
            + "order that left a machine with no broker and a half-finished install");
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_also_refuses_a_broker_somebody_started_by_hand(string script)
    {
        // Both scripts leave a hand-started broker alone on purpose -- ending a process a person
        // launched themselves is not a script's business. But it holds the binary exactly as
        // firmly as one an autostart entry started, so without this the uninstall fails on a
        // denied path and leaves the tools half replaced. Checked after the stop, which is the
        // only point where the two can be told apart.
        var text = RepositoryFile.Read(script);

        var post = text.IndexOf(
            "still holding the tools after the broker was stopped", StringComparison.Ordinal);

        var install = text.IndexOf("Installing version", StringComparison.Ordinal);

        Assert.True(post >= 0, $"{script} does not look for a stray broker after stopping");
        Assert.True(
            post < install,
            $"{script} checks for a stray broker after it has begun replacing files");
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Neither_script_hardcodes_a_tool_command_in_its_guards(string script)
    {
        // The guards match on process names, and a name that matches nothing looks exactly like a
        // machine with nothing running -- so a rename would disable them silently. Both derive
        // the names from the list each script already keeps, and this is what says they still do.
        var text = RepositoryFile.Read(script);

        Assert.DoesNotContain("pgrep -x parley-mcp", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Process -Name 'parley-mcp'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_readme_sends_a_new_user_to_the_scripts()
    {
        // The scripts are only worth having if the path a new reader follows leads to them.
        // Documented steps beside a script that does the same thing is how the two come to
        // disagree, and the documented one is always the one that rots.
        var readme = RepositoryFile.Read("README.md");

        Assert.Contains("install.ps1", readme, StringComparison.Ordinal);
        Assert.Contains("install.sh", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_readme_no_longer_walks_through_the_commands_by_hand()
    {
        // The pair of commands the script replaced. Left in the README they would be a second,
        // subtly different path -- without the clearing and without the check -- and a reader
        // has no way to tell which one is current.
        var readme = RepositoryFile.Read("README.md");

        Assert.DoesNotContain(
            "dotnet pack src/Parley.Mcp -c Release", readme, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "dotnet pack src/Parley.Cli -c Release", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_scripts_say_what_to_do_next()
    {
        // Installing the tools is not the end of setting Parley up, and a script that stopped
        // there would leave somebody with two commands on PATH and nothing working -- which is
        // the state a broker being a separate process creates and the one worth naming.
        //
        // What to do next is the terminal's flow since 2026-09-27: make a project, then enrol agents into it
        // from a folder. It was /parley:setup in each repository and `parley registry` to check -- and this
        // test required exactly that, so it held the outdated steps in place until the owner read them.
        foreach (var script in new[] { Windows, Unix })
        {
            var code = Code(script);

            Assert.Contains("Next, run:  parley", code, StringComparison.Ordinal);
            Assert.Contains("Enroll agents from folders", code, StringComparison.Ordinal);
            Assert.DoesNotContain("/parley:setup", code, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_sets_the_broker_up_rather_than_describing_it(string script)
    {
        // Both scripts used to end by printing the autostart command for somebody to copy, and
        // the step nobody ran is the step that was printed. Every agent then failed to attach
        // with "No broker is listening" -- in an editor, later, a long way from here.
        var text = RepositoryFile.Read(script);

        Assert.Contains("autostart install", Code(text), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void No_script_asks_where_a_store_goes_or_takes_a_catalogue(string script)
    {
        // Where a deployment lives is a project's to say, and a project is named in `parley terminal` or with
        // `parley project add`. The scripts used to ask for a store when nothing was named -- registering one deployment by
        // explicit paths -- and, given one, to register that narrow entry over a working manifest: a reinstall that
        // stopped serving every project but one. The owner removed the parameter rather than guard it.
        var code = Code(RepositoryFile.Read(script));

        // **`--db`, `--listen` and `PARLEY_DB` joined this list in 019**, which is what makes their
        // removal hold: both scripts printed `project add <name> --db <path> --listen <url>` as the
        // closing "name your first project" line, and prose that tells somebody to type a flag the tool
        // refuses is worse than no prose at all.
        foreach (var gone in new[]
                 {
                     "$Store", "-Store", "Resolve-Store", "$NodeConfig", "-NodeConfig", "--store",
                     "$store", "resolve_store", "node_config", "--node-config",
                     "--db", "--listen", "PARLEY_DB",
                 })
        {
            Assert.DoesNotContain(gone, code, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void With_no_project_named_each_script_says_how_to_name_the_first(string script)
    {
        // A machine just installed names nothing, and its broker is waiting for the first project. The script is the
        // last thing the person read, so it says the next step rather than leaving it to be found.
        var code = Code(RepositoryFile.Read(script));

        // **And the terminal is the only next step there is now.** `parley project add` went on
        // 2026-09-25, so a script still offering it would be handing somebody a command the tool
        // refuses, at the one moment they have nothing else to go on.
        Assert.Contains("No projects configured", code, StringComparison.Ordinal);

        // **And the steps are the terminal's flow, spelled the way it is typed.** The owner caught both
        // halves on 2026-09-27: the script ended by sending people to /parley:setup in each repository and
        // to `parley registry`, which is the flow from before a project could enrol a folder -- and it named
        // the terminal `parley terminal`, which is not what anybody types.
        Assert.Contains("Next, run:  parley", code, StringComparison.Ordinal);
        Assert.Contains("Enroll agents from folders", code, StringComparison.Ordinal);
        Assert.DoesNotContain("/parley:setup", code, StringComparison.Ordinal);
        Assert.DoesNotContain("parley terminal", code, StringComparison.Ordinal);
        Assert.DoesNotContain("parley project add", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_script_counts_a_version_it_could_not_read_as_a_failure()
    {
        // `parley-mcp --version` answered only with a launch configuration, so it exited 78 and printed nothing on
        // standard output. PowerShell's `-notmatch` over that empty result is empty, which is false, and the check
        // passed; the POSIX script's bare substitution would have ended it under set -e with no message at all.
        Assert.Contains("$LASTEXITCODE -ne 0 -or $says -notmatch", Code(Windows), StringComparison.Ordinal);
        Assert.Contains("--version 2>&1)\" || says=\"\"", Code(Unix), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_checks_the_broker_answers_rather_than_that_it_was_registered(
        string script)
    {
        // The same distinction as checking the version on PATH: "the entry was created" and "an
        // agent can attach" are different claims, and only the second is the one being made.
        // What proves it is the poll that reads `project list` for a broker answering.
        //
        // **This used to find the last `registry` in the script**, which was a line telling the person to
        // run `parley registry` -- advice, not the check. When that advice went the test failed while the
        // real check stood untouched, which is the proxy measuring the wrong thing from the other side.
        var code = Code(RepositoryFile.Read(script));
        var registers = code.IndexOf("autostart install", StringComparison.Ordinal);
        var proves = code.LastIndexOf("answering", StringComparison.Ordinal);

        Assert.True(
            registers < proves,
            $"{script} does not check that the broker answers after registering it, so a "
            + "registration that produced no running broker would still report success.");
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_removes_the_autostart_entry_it_created(string script)
    {
        // Asymmetry worth failing over. The scripts register a supervisor entry naming a path,
        // and uninstalling deletes the executable at that path -- so leaving the entry behind
        // leaves something that fails at every logon, silently, where nobody looks.
        var code = Code(RepositoryFile.Read(script));

        Assert.Contains("autostart remove", code, StringComparison.Ordinal);

        var removes = code.IndexOf("autostart remove", StringComparison.Ordinal);
        var deletes = code.IndexOf("tool uninstall", StringComparison.Ordinal);

        Assert.True(
            removes < deletes,
            $"{script} deletes the tools before removing the entry that names them, which leaves "
            + "a supervisor pointing at an executable that is no longer there.");
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_stops_the_broker_before_replacing_its_binary(string script)
    {
        // Found by probing rather than by reading. A reinstall over a live broker reported
        // complete success and installed nothing: Windows will not unlink a mapped binary, so
        // `dotnet tool uninstall` failed, and the install after it did nothing while exiting 0.
        var code = Code(RepositoryFile.Read(script));
        var stops = code.IndexOf("top", StringComparison.Ordinal);
        var installs = code.IndexOf("tool install", StringComparison.Ordinal);

        Assert.True(
            stops >= 0 && stops < installs,
            $"{script} installs over whatever is running. A broker holding the files makes the "
            + "uninstall fail and the install a no-op, and both report success.");
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_refuses_an_install_that_installed_nothing(string script)
    {
        // The second door into the same silent no-op, and the one the version check cannot see.
        // `dotnet tool install` exits 0 for a package that is already present, prints "is
        // already installed", and replaces nothing -- so a failed uninstall followed by that is
        // indistinguishable from success unless the message itself is treated as the failure it
        // is. The version check afterwards finds exactly the version it expected.
        var text = RepositoryFile.Read(script);

        Assert.Contains("already installed", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_calls_the_autostart_entry_what_the_code_calls_it(string script)
    {
        // Both scripts stop the broker themselves, which means naming the supervisor entry --
        // and the only command that could tell them its name is the one they may be in the
        // middle of deleting. So the name is duplicated, and this is what keeps the copy honest:
        // renaming it in AutostartPlan would otherwise leave two scripts stopping nothing,
        // silently, and going on to install over a broker that is still running.
        var text = RepositoryFile.Read(script);

        Assert.Contains(AutostartPlan.Name, text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void No_script_deletes_the_store(string script)
    {
        // Uninstalling is for the installation. A person reinstalling a tool has not asked to
        // lose every exchange their deployment has ever had, and a script that took the store
        // with it would be the only thing here whose mistake could not be undone.
        var text = RepositoryFile.Read(script);

        Assert.DoesNotContain(
            Executable(text),
            line => line.Contains("parley.db", StringComparison.Ordinal)
                && (line.Contains("Remove-Item", StringComparison.Ordinal)
                    || line.Contains("rm ", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_can_register_an_entry_that_opens_no_window(string script)
    {
        // **The scripts register the entry themselves, which is what made this a gap.** A person
        // installing Parley never types `autostart install`; the script does it for them. So a
        // script with no way to ask for `--hidden` is a machine that gets a console window at
        // every logon, and the only remedy is the one command the terminal cannot run -- on a
        // deployment whose whole direction is that setting up happens on a screen, not in a shell.
        //
        // Reported by the owner as exactly that: "I am not going to use the CLI".
        var code = Code(RepositoryFile.Read(script));

        Assert.Contains("--hidden", code, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Neither_script_hides_the_window_unless_it_was_asked_to(string script)
    {
        // **Opt-in, and the check is that the flag sits behind a condition.** A script that always
        // passed it would change what every machine already installed does at logon, which is a
        // decision for the person running it rather than for whoever edited the script -- and the
        // surprise would be silent, since the symptom is a window that stops appearing.
        //
        // Checked as "the flag is never on the line that runs the command". A script that wired it
        // into the invocation would pass it on every run, and that is the one shape this has to
        // refuse. A first draft of this test asked instead whether the flag sat on a line
        // containing `if` or `=`, which every reasonable line does -- it could not have failed.
        var lines = Executable(RepositoryFile.Read(script));

        var wired = lines
            .Where(line => line.Contains("autostart install", StringComparison.Ordinal)
                && line.Contains("--hidden", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            wired.Length == 0,
            $"{script} passes --hidden on the line that registers the entry, so every install "
            + $"would get it: {string.Join(" | ", wired)}");
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void A_web_UI_the_broker_started_does_not_refuse_the_install(string script)
    {
        // **Reported by the owner: an ordinary machine refused its own install.** The broker was
        // up, it had started `parley-web-ui` beside itself as it is meant to, and the pre-flight
        // counted that child as a process somebody had to go and close — naming a pid with no
        // window and offering -Force to end something the script was three steps from ending
        // itself. The broker is excluded from that check for exactly this reason, in a comment
        // that says so; the web UI was simply never added to it.
        //
        // Checked as "the exclusion is written", because what it guards is a process tree this
        // suite cannot build. A hand-started web UI is still caught, after the broker stops, by
        // the sweep over every replacing command — so this defers the question rather than
        // dropping it.
        var code = Code(RepositoryFile.Read(script));

        Assert.Contains("parley-web-ui", code, StringComparison.Ordinal);

        var excluded = script.EndsWith(".ps1", StringComparison.Ordinal)
            ? code.Contains("$_ -ne $webUiCommand", StringComparison.Ordinal)
            : code.Contains("\"${commands[$i]}\" = \"parley-web-ui\" ] && [ \"$replacing_broker\"", StringComparison.Ordinal);

        Assert.True(
            excluded,
            $"{script} counts the broker's own web UI as a holder, so a machine with a running "
            + "broker refuses its own install");
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Stopping_the_broker_waits_for_the_web_UI_it_started(string script)
    {
        // **The other half, and it is the dangerous one.** The broker ends its child, so the child
        // goes — a moment later. Continuing straight on runs `dotnet tool uninstall` against a
        // binary that may still be mapped, which fails; `dotnet tool install` then exits *zero*
        // saying "is already installed"; and the version check cannot tell, because the version it
        // finds is the version it expected. This script's own comments call that its worst
        // failure, and excluding the web UI from the guard above is what would have opened it.
        var code = Code(RepositoryFile.Read(script));

        // Two waits, not one. A first version waited for the broker and the web UI together,
        // which spends the whole budget on exactly the machine this exists to help: an orphaned
        // web UI never exits on its own, so the combined wait runs to its deadline every time and
        // only then reaches the code that ends it.
        var waitsForBroker = script.EndsWith(".ps1", StringComparison.Ordinal)
            ? code.Contains("Get-Process -Name 'parley' -ErrorAction", StringComparison.Ordinal)
            : code.Contains("-z \"$(holding parley)\"", StringComparison.Ordinal);

        var waitsForWebUi = script.EndsWith(".ps1", StringComparison.Ordinal)
            ? code.Contains("Get-Process -Name 'parley-web-ui' -ErrorAction", StringComparison.Ordinal)
            : code.Contains("-z \"$(holding parley-web-ui)\"", StringComparison.Ordinal);

        Assert.True(
            waitsForBroker && waitsForWebUi,
            $"{script} stops the broker without waiting for it and for the web UI it started, so "
            + "the uninstall can race a binary that is still mapped");
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Stopping_the_broker_clears_the_web_UI_it_orphaned(string script)
    {
        // **The loop the owner was stuck in.** Stopping a scheduled task or a systemd unit
        // terminates the broker rather than asking it to exit, so it never reaches the code that
        // ends the child it started. The child keeps running, keeps the port and keeps the binary
        // busy — and then refuses the very install that stopped the broker. Same pid, twice, in a
        // refusal naming a process they had never started and could not see.
        //
        // The test is that the parent is consulted. Ending every `parley-web-ui` by name would
        // reach one somebody started themselves, which is the thing this script refuses to do
        // anywhere else.
        var code = Code(RepositoryFile.Read(script));

        var byParent = script.EndsWith(".ps1", StringComparison.Ordinal)
            ? code.Contains("ParentProcessId", StringComparison.Ordinal)
            : code.Contains("ps -o ppid=", StringComparison.Ordinal);

        Assert.True(
            byParent,
            $"{script} does not clear the web UI it orphans when it stops the broker, or does not "
            + "tell an orphan from one somebody started");
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void The_orphan_is_waited_for_after_it_is_ended(string script)
    {
        // **A kill asks; it does not wait.** `Remove-Holders` carries a whole paragraph about
        // this, and the first version of the orphan cleanup reintroduced it twenty lines above
        // that paragraph: the script printed "ending the web UI the stopped broker left behind,
        // pid 26996" and then refused the install naming pid 26996, in consecutive lines.
        //
        // Read as ordering: the wait for the web UI must come after the code that ends it. A wait
        // before the kill is the defect, and it is the arrangement a careless edit produces.
        var code = Code(RepositoryFile.Read(script));

        var ends = script.EndsWith(".ps1", StringComparison.Ordinal)
            ? code.IndexOf("Stop-Process -Id $stray.ProcessId", StringComparison.Ordinal)
            : code.IndexOf("ending the web UI the stopped broker left behind", StringComparison.Ordinal);

        // Anchored on the *wait*, not on the name: install.sh names `holding parley-web-ui` twice,
        // once to enumerate the orphans and once to wait for them, and the enumeration comes
        // first. A looser anchor found that one and reported the ordering defect against a script
        // that had it right — which is the same class of mistake as the defect itself.
        var waits = script.EndsWith(".ps1", StringComparison.Ordinal)
            ? code.IndexOf("Get-Process -Name 'parley-web-ui' -ErrorAction", StringComparison.Ordinal)
            : code.IndexOf("-z \"$(holding parley-web-ui)\"", StringComparison.Ordinal);

        Assert.True(ends >= 0, $"{script} no longer ends the web UI it orphaned");
        Assert.True(waits >= 0, $"{script} never waits for the web UI to go");
        Assert.True(
            waits > ends,
            $"{script} waits for the web UI before ending it, so the kill is announced and the "
            + "install then refuses on the process it just killed");
    }

    [Fact]
    public void The_help_reaches_the_usage_and_not_only_the_preamble()
    {
        // **`./install.sh --help` showed not one flag.** It printed lines 2 to 25 — the paragraphs
        // explaining why the script exists — and the usage list starts at line 27. So the one part
        // anybody runs `--help` for was the part it stopped immediately before.
        //
        // Found by running the script in a Linux container, which is the thing this class says in
        // its own summary it cannot do. It had been true for as long as the flags have existed.
        //
        // The fix reads to the first non-comment line rather than to a number, so a paragraph
        // added to the header is in the help by construction. This checks that: no line range, and
        // every flag the parser takes named in the header it prints.
        var text = RepositoryFile.Read("install.sh");

        // Executable lines only. The comment explaining this fix necessarily quotes the range it
        // replaced, and a check over the whole file is answered by the sentence describing the
        // defect — which is the trap this repository records against removal stubs naming the verb
        // they removed, met again here by the person writing the guard.
        Assert.DoesNotContain("sed -n '2,25p'", Code(text), StringComparison.Ordinal);

        var header = string.Join('\n', text.Split('\n').TakeWhile(line => line.StartsWith('#') || line.Length == 0));

        foreach (var flag in new[] { "--uninstall", "--surfaces", "--only", "--force", "--hidden" })
        {
            Assert.Contains(flag, header, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_leaves_the_terminal_able_to_take_typing(string script)
    {
        // **Reported three runs running: every mouse movement printing `[555;47;16M` at the
        // prompt.** A terminal left in mouse-reporting mode, which belongs to the window rather
        // than to any process and so outlives whatever set it.
        //
        // This script was established not to be what sets it — one of those runs refused before
        // executing a single Parley binary and produced it anyway — so what it does is clear it.
        // Disabling a mode that is already off does nothing, which makes this free on a healthy
        // terminal and a repair on a stuck one, and means a terminal ruined after running this
        // cannot be this script's doing.
        var code = Code(RepositoryFile.Read(script));

        foreach (var mode in new[] { 1003, 1015, 1006 })
        {
            Assert.Contains($"?{mode}l", code, StringComparison.Ordinal);
        }

        // The alternate screen is deliberately not among them: switching back to the main screen
        // would throw away the screen of somebody running this from inside a full-screen program.
        Assert.DoesNotContain("?1049l", code, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_declares_the_switch_a_person_passes(string script)
    {
        // The other half of the pair above: "not wired into the call" is satisfied perfectly by a
        // script that cannot pass the flag at all, which is the state this change started from. So
        // the switch itself is named, in the place each shell declares one.
        var code = Code(RepositoryFile.Read(script));

        var declared = script.EndsWith(".ps1", StringComparison.Ordinal)
            ? code.Contains("[switch] $Hidden", StringComparison.Ordinal)
            : code.Contains("--hidden) hidden=1", StringComparison.Ordinal);

        Assert.True(declared, $"{script} names --hidden somewhere but does not take it as an argument");
    }
}
