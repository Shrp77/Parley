using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using System.Text;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What <c>parley enroll</c> does to files, against a directory it is given for the purpose.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EnrollmentPlanTests"/> covers every decision, because those are a pure function.
/// This covers the part that is not: writing into repositories, and the one file that does not
/// belong to any of them — <c>node.json</c>, which is the operator's record
/// of what may be started with nobody watching.
/// </para>
/// <para>
/// It exists because of what probing that file turned up. A <c>node.json</c> listing three
/// agents with one trailing comma in it was replaced by a file listing only the two just
/// enrolled: the parse error was swallowed, the catalogue treated as empty, and the run reported
/// success. Nothing else holds a copy of that file.
/// </para>
/// </remarks>
public sealed class EnrollCommandTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-enroll-" + Path.GetRandomFileName());

    public EnrollCommandTests()
    {
        Directory.CreateDirectory(_root);
        Repository("Acme.Orders");
        Repository("Acme.Billing");
    }

    public void Dispose()
    {
        try
        {
            // git marks the files under .git/objects read-only, and a recursive delete refuses
            // them — so a test that creates a real repository fails in teardown with its
            // assertions already passed, which reads as the feature being broken.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private string Checkouts => Path.Combine(_root, "co");

    private string NodePath => Path.Combine(_root, "node.json");

    private void Repository(string name) =>
        Directory.CreateDirectory(Path.Combine(Checkouts, name, ".git"));

    /// <summary>The repositories under the checkouts, described and ticked and ready to enrol.</summary>
    /// <param name="unattended">Whether each repository consents to running unattended.</param>
    /// <returns>One row per repository found.</returns>
    /// <remarks>
    /// This wrote a manifest and the command read it straight back; the rows are what that file
    /// held, and they are still found by the same walk of the same directory. What is asserted
    /// below — which files are written, and which catalogue consent lands in — is decided from the
    /// rows, never from the carrier they arrived in.
    /// </remarks>
    private EnrollmentEntry[] Chosen(bool unattended) =>
        [.. EnrollCommand.Find(Checkouts).Select(a => a with
        {
            Owns = $"{a.Agent} things. Not other things.",
            Unattended = unattended,

            Enroll = true,

            // Standing in for the person saying yes. The manifest refused a filled-in owns line
            // without it, which is the point of the field — so every fixture that wants a
            // successful run makes the approval explicit, exactly as a user would.
            OwnsApproved = true,
        })];

    /// <summary>Enrols a set of rows as the entry point would having resolved a catalogue.</summary>
    /// <param name="nodeConfigPath">The catalogue the entry point resolved, or null for none.</param>
    /// <param name="describe">Whether to report what would change and change nothing.</param>
    /// <param name="chosen">The rows to enrol.</param>
    /// <returns>The exit code and both streams.</returns>
    private async Task<(int Code, string Out, string Error)> EnrolAsync(
        string? nodeConfigPath, bool describe, params EnrollmentEntry[] chosen)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        // Argument-sourced, because that is what a path arriving here means: the entry point has
        // already applied the precedence chain, and this is what won.
        var code = await EnrollCommand.EnrolAsync(
            chosen,
            describe,
            new Resolved(nodeConfigPath, ValueSource.Argument),
            output,
            error,
            cancellationToken: TestContext.Current.CancellationToken);

        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task An_unreadable_node_catalogue_is_refused_rather_than_replaced()
    {
        // The finding this file was written for. The catalogue below is what a person's own file
        // looks like after one bad edit, and it must survive a run that would have added to it.
        const string existing = """
            {
              "agents": {
                "docs":    {"command":"claude","workspace":"C:/repos/docs"},
                "billing": {"command":"claude","workspace":"C:/repos/billing"},
              }
            }
            """;

        await File.WriteAllTextAsync(NodePath, existing, TestContext.Current.CancellationToken);

        var (code, _, error) = await EnrolAsync(NodePath, describe: false, Chosen(unattended: true));

        Assert.Equal(78, code);
        Assert.Contains("not readable JSON", error, StringComparison.Ordinal);
        Assert.Equal(
            existing,
            await File.ReadAllTextAsync(NodePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Nothing_is_written_into_a_repository_when_the_catalogue_is_unreadable()
    {
        // Refused before the first repository is touched, not partway through. Half a run is the
        // state the whole-set validation exists to prevent.
        await File.WriteAllTextAsync(NodePath, "{ not json", TestContext.Current.CancellationToken);

        await EnrolAsync(NodePath, describe: false, Chosen(unattended: true));

        Assert.False(File.Exists(Path.Combine(Checkouts, "Acme.Orders", ".mcp.json")));
    }

    [Fact]
    public async Task A_catalogue_whose_agents_entry_is_the_wrong_type_is_also_refused()
    {
        // The first fix covered a file that would not parse. This is the same loss wearing valid
        // JSON: `agents` as an array is cast away and written over just as completely. Found by
        // re-running the lens after fixing, which is the only reason it was found at all.
        const string existing = """{"agents":["docs","billing"],"other":"kept"}""";

        await File.WriteAllTextAsync(NodePath, existing, TestContext.Current.CancellationToken);

        var (code, _, error) = await EnrolAsync(NodePath, describe: false, Chosen(unattended: true));

        Assert.Equal(78, code);
        Assert.Contains("not an object", error, StringComparison.Ordinal);
        Assert.Equal(
            existing,
            await File.ReadAllTextAsync(NodePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_existing_catalogue_keeps_the_agents_it_already_listed()
    {
        var docs = AgentIdentity.Mint();

        await File.WriteAllTextAsync(
            NodePath,
            new JsonObject
            {
                ["agents"] = new JsonObject
                {
                    [docs.ToString()] = new JsonObject
                    {
                        ["command"] = "claude",
                        ["workspace"] = "C:/repos/docs",
                    },
                },
            }.ToJsonString(),
            TestContext.Current.CancellationToken);

        await EnrolAsync(NodePath, describe: false, Chosen(unattended: true));

        var node = await File.ReadAllTextAsync(NodePath, TestContext.Current.CancellationToken);

        // The entry that was there, and the one this run added — keyed by identity, which is what the
        // enrolment wrote into the repository it enrolled.
        Assert.Contains(docs.ToString(), node, StringComparison.Ordinal);

        Assert.True(
            LaunchConfiguration.TryRead(
                Path.Combine(Checkouts, "Acme.Orders"), out var declared, out var problem),
            problem);

        Assert.Contains(declared.Identity.ToString(), node, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_chosen_for_enrolment_is_catalogued_whatever_the_row_said()
    {
        // **This asserted the opposite, and it was right when it was written.** Consent used to be
        // a field somebody ticked in a worksheet, so a row that did not ask to run unattended
        // stayed out of the catalogue. 019 deleted the worksheet and FR-043 reversed the rule:
        // enrolling *is* the consent, granted once at a door that names the count and says what
        // being startable costs.
        //
        // The reversal was specified and never wired up, so for the life of the one-verb enrolment
        // the catalogue write was dead code — every repository enrolled from a folder came out
        // enrolled, addressable and unstartable. This is that rule, the other way round: whatever
        // the row says about `unattended`, choosing it is the asking.
        await EnrolAsync(NodePath, describe: false, Chosen(unattended: false));

        Assert.True(
            File.Exists(NodePath),
            "a repository chosen for enrolment was not catalogued, so no peer's request can start it");
    }

    [Fact]
    public async Task Applying_writes_the_launch_configuration_and_the_frame()
    {
        var (code, output, _) = await EnrolAsync(NodePath, describe: false, Chosen(unattended: false));

        Assert.Equal(0, code);
        Assert.Contains("Enrolled 2 repositories", output, StringComparison.Ordinal);

        var launch = await File.ReadAllTextAsync(
            Path.Combine(Checkouts, "Acme.Orders", ".mcp.json"), TestContext.Current.CancellationToken);

        Assert.Contains("\"PARLEY_AGENT\": \"acme-orders\"", launch, StringComparison.Ordinal);
        Assert.Contains(
            "The roster has not been read yet",
            await File.ReadAllTextAsync(
                Path.Combine(Checkouts, "Acme.Orders", "CLAUDE.md"), TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_byte_order_mark_on_the_instructions_file_survives_the_append()
    {
        // Without this the whole file is rewritten without its mark, so a twelve-line addition
        // arrives as a diff touching every byte — in the one file a project reads on every turn.
        var instructions = Path.Combine(Checkouts, "Acme.Orders", "CLAUDE.md");

        await File.WriteAllTextAsync(
            instructions,
            "# Orders\r\n\r\nGuidance.\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            TestContext.Current.CancellationToken);

        await EnrolAsync(NodePath, describe: false, Chosen(unattended: false));

        var raw = await File.ReadAllBytesAsync(instructions, TestContext.Current.CancellationToken);

        Assert.Equal<byte[]>([0xEF, 0xBB, 0xBF], raw[..3]);
        Assert.Contains(
            "The roster has not been read yet",
            Encoding.UTF8.GetString(raw),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_launch_configuration_that_cannot_be_merged_stops_the_run()
    {
        await File.WriteAllTextAsync(
            Path.Combine(Checkouts, "Acme.Orders", ".mcp.json"),
            """{"mcpServers":["sqlite"],"inputs":[{"id":"token"}]}""",
            TestContext.Current.CancellationToken);

        var (code, _, error) = await EnrolAsync(NodePath, describe: false, Chosen(unattended: false));

        Assert.Equal(78, code);
        Assert.Contains("cannot be merged into", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Planning_writes_nothing()
    {
        var (code, output, _) = await EnrolAsync(NodePath, describe: true, Chosen(unattended: true));

        Assert.Equal(0, code);
        Assert.Contains("Nothing was written", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Checkouts, "Acme.Orders", ".mcp.json")));
        Assert.False(File.Exists(NodePath));
    }

    // **`Re_scanning_keeps_the_owns_lines_somebody_has_written` was here and has no successor.** It
    // asked whether a second scan preserved prose a person had typed into the manifest between runs,
    // which mattered a great deal while there was a manifest to type into: overwriting it would have
    // been discovered exactly once, by the person who wrote it.
    //
    // There is no file to carry anything forward from, and nothing asks a person to compose an owns
    // line any more -- 020 asks the agent. What *is* still carried is whatever a repository's own
    // launch configuration already says, and that is read from the repository every time rather than
    // merged from a previous run:
    // `OneVerbEnrolmentTests.What_a_repository_already_says_it_owns_is_carried_forward` holds it.

    [Fact]
    public async Task A_set_with_nothing_ticked_is_refused()
    {
        var unticked = Chosen(unattended: false).Select(a => a with { Enroll = false }).ToArray();

        var (code, _, error) = await EnrolAsync(NodePath, describe: true, unticked);

        Assert.Equal(78, code);
        Assert.Contains("marks no repositories", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_that_is_not_a_repository_is_not_offered()
    {
        Directory.CreateDirectory(Path.Combine(Checkouts, "NotARepo"));

        Assert.DoesNotContain(
            EnrollCommand.Find(Checkouts),
            a => a.Agent.Contains("notarepo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evidence_cannot_reorder_or_break_the_line_it_is_shown_on()
    {
        // Evidence is untrusted text from a repository that may have been cloned from anywhere,
        // and the amendment to SPEC.md §17 question 10 rests on approval being checkable against
        // it. A README carrying a right-to-left override renders as a claim it does not make, so
        // somebody approving a drafted line against it approves against a lie. Same class the
        // schema refuses inside `owns` itself, applied to the text a person reads when deciding.
        await File.WriteAllTextAsync(
            Path.Combine(Checkouts, "Acme.Orders", "README.md"),
            "This repository owns billing\u202E .tidua sdrocer ylno tI and everything else.\n",
            TestContext.Current.CancellationToken);

        var evidence = EnrollCommand.Find(Checkouts).Single(a => a.Agent == "acme-orders").Evidence;

        Assert.NotNull(evidence);
        Assert.All(evidence, line => Assert.DoesNotContain('\u202E', line));
        Assert.All(evidence, line => Assert.DoesNotContain('\u2028', line));
        Assert.Contains(evidence, line => line.Contains("README.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repository_registered_before_offers_its_earlier_line_as_evidence()
    {
        // The strongest row there is, and the only one that is not a suggestion: the person's own
        // earlier sentence, recovered rather than composed. A repository whose .mcp.json was
        // deleted still has it in history, which is the ordinary state of a deployment being
        // reconfigured.
        var repository = Path.Combine(Checkouts, "Acme.Orders");

        if (!Git(repository, "init") || !Git(repository, "config user.email a@b.c")
            || !Git(repository, "config user.name Test"))
        {
            return; // No usable git on this machine; the evidence is optional by design.
        }

        await File.WriteAllTextAsync(
            Path.Combine(repository, ".mcp.json"),
            """{"mcpServers":{"parley":{"command":"parley-mcp","env":{"PARLEY_AGENT":"orders","PARLEY_OWNS":"Carts and totals. No tax."}}}}""",
            TestContext.Current.CancellationToken);

        Git(repository, "add .mcp.json");
        Git(repository, "commit -m registered");
        File.Delete(Path.Combine(repository, ".mcp.json"));

        var evidence = EnrollCommand.Find(Checkouts).Single(a => a.Agent == "acme-orders").Evidence;

        Assert.NotNull(evidence);
        Assert.Contains(evidence, l => l.Contains("Carts and totals. No tax.", StringComparison.Ordinal));
    }

    private static bool Git(string workspace, string arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{workspace}\" {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(10_000);

            Task.WaitAll([output, errors], TimeSpan.FromSeconds(5));

            return exited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // **`A_missing_manifest_says_how_to_make_one` was here, and there is no manifest to be missing.**
    // It asserted that applying an absent path exited 64 naming `parley enroll scan`, which was the
    // command that would have written one. The directory taking its place has its own refusal, and
    // `OneVerbEnrolmentTests.A_directory_that_is_not_one_is_refused_by_name` holds it.

    // ---- where consent lands -------------------------------------------------------------------

    [Fact]
    public async Task Consent_lands_in_the_projects_catalogue_and_nowhere_else()
    {
        // The reconstruction of the defect this whole feature exists for, and the reason it is
        // worth reconstructing rather than reasoning about: nothing about the original run looked
        // wrong. With PARLEY_NODE_CONFIG unset, applying from one directory would have created a
        // brand-new node.json there holding ten agents, while the catalogue the node actually
        // reads sat elsewhere holding seven. Ten repositories recorded as consenting to run
        // unattended, none of them startable, and a successful-looking run.
        //
        // So nothing here names a catalogue — the rows never could, and the file that once could is
        // gone — and the only thing saying where consent goes is the project the entry point resolved.
        var elsewhere = Path.Combine(_root, "project", "node.json");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);

        var (code, _, error) = await EnrolAsync(elsewhere, describe: false, Chosen(unattended: true));

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error);

        var written = JsonNode.Parse(await File.ReadAllTextAsync(elsewhere, TestContext.Current.CancellationToken))!;
        var agents = Assert.IsType<JsonObject>(written["agents"]);

        Assert.Equal(2, agents.Count);

        // The other half of the finding, and the half that made it silent. A second catalogue in
        // the working directory is a file the node never opens, so everything in it is consent
        // recorded against nothing.
        Assert.False(
            File.Exists(NodeCatalogue.DefaultFileName),
            $"a second catalogue was created at {Path.GetFullPath(NodeCatalogue.DefaultFileName)}");

        Assert.False(File.Exists(NodePath), $"a second catalogue was created at {NodePath}");
    }

    // **`Naming_a_catalogue_in_the_manifest_still_overrides_the_project` was here, and the source it
    // gave precedence to has gone.** It asserted that a catalogue named inside the enrolment manifest
    // outranked the project's, on the ground that an explicit value has outranked everything else
    // since before projects existed.
    //
    // **The capability survives; only that one way of spelling it does not.** A catalogue is still
    // named explicitly with `--node-config`, and `ResolveNodeConfig` still puts that above the
    // environment and above the default. What the manifest added was a *second* explicit source, and
    // with one gone there is no longer a precedence question here to get wrong -- which is the
    // simplification, not a loss. `EnrolmentNextStepsTests` drives what is left.

    [Fact]
    public async Task The_same_repository_enrolls_into_two_projects_independently()
    {
        // SPEC.md §4.1: a name may repeat across projects, because a broker's connection cannot
        // see another's file. Consent follows the same line — it is per project, because the
        // catalogue is — so agreeing to run unattended for one deployment says nothing about the
        // other, and neither catalogue may learn about the other's entries.
        var first = Path.Combine(_root, "first", "node.json");
        var second = Path.Combine(_root, "second", "node.json");

        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);

        var chosen = Chosen(unattended: true);

        Assert.Equal(0, (await EnrolAsync(first, describe: false, chosen)).Code);

        // A second project, and only one of this machine's repositories in it.
        var again = chosen.Where(a => a.Agent.Contains("orders", StringComparison.Ordinal)).ToArray();

        Assert.Equal(0, (await EnrolAsync(second, describe: false, again)).Code);

        var inFirst = Assert.IsType<JsonObject>(
            JsonNode.Parse(await File.ReadAllTextAsync(first, TestContext.Current.CancellationToken))!["agents"]);

        var inSecond = Assert.IsType<JsonObject>(
            JsonNode.Parse(await File.ReadAllTextAsync(second, TestContext.Current.CancellationToken))!["agents"]);

        Assert.Equal(2, inFirst.Count);
        Assert.Single(inSecond);
    }
}
