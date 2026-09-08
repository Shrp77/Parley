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
        foreach (var script in new[] { Windows, Unix })
        {
            Assert.Contains("/parley:setup", script, StringComparison.Ordinal);
            Assert.Contains("parley registry", script, StringComparison.Ordinal);
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
    public void Each_script_takes_the_store_as_an_argument(string script)
    {
        // Where the store goes is the one decision this cannot make for somebody, because it
        // outlives every clone and holds the whole deployment's history. Taking it as an
        // argument is also what makes the scripts usable unattended.
        var text = RepositoryFile.Read(script);

        Assert.Matches(@"(--db|\$Db)", text);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_defaults_the_store_outside_this_checkout(string script)
    {
        // Under the home directory, never beside the repository. A store inside a working copy
        // is destroyed by `git clean`, and a store inside *this* working copy would make
        // Parley's own checkout load-bearing for machines that do not have one.
        var text = RepositoryFile.Read(script);

        Assert.Contains(".parley", text, StringComparison.Ordinal);
        Assert.Matches(@"(USERPROFILE|\$HOME)", text);
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_checks_the_broker_answers_rather_than_that_it_was_registered(
        string script)
    {
        // The same distinction as checking the version on PATH: "the entry was created" and "an
        // agent can attach" are different claims, and only the second is the one being made.
        // `parley registry` is a client, so it can only succeed against a broker that is up.
        var code = Code(RepositoryFile.Read(script));
        var registers = code.IndexOf("autostart install", StringComparison.Ordinal);
        var proves = code.LastIndexOf("registry", StringComparison.Ordinal);

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
    [Fact]
    public void Reinstalling_on_Windows_keeps_the_agent_catalogue_it_is_replacing()
    {
        // `autostart install` registers a broker-alone entry when given no catalogue, so a script
        // that always calls it that way turns a working deployment into one whose agents are
        // never started -- and the loss arrives days later, on a machine nobody is signed into,
        // as a question that waits out its window and is ghosted.
        //
        // The operator ran the script and is reading its output, not the command's, so the
        // command's own warning does not reach them.
        Assert.Contains("--node-config", Windows, StringComparison.Ordinal);
        Assert.Contains("$existingArguments", Windows, StringComparison.Ordinal);
        Assert.Contains(
            "keeping this machine's agent catalogue", Windows, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_scripts_take_a_catalogue_and_say_what_happens_without_one()
    {
        // Read-back is Windows-only, because the POSIX script registers a systemd user unit or a
        // launch agent and `autostart status` reports neither's arguments. So the Unix half says
        // plainly that the entry will start the broker alone -- which is better than a read-back
        // that works on one platform and silently does nothing on the other.
        Assert.Contains("--node-config", Unix, StringComparison.Ordinal);
        Assert.Contains("starts the broker alone", Unix, StringComparison.Ordinal);
    }

}
