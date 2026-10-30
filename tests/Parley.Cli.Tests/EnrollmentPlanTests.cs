using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What enrolling repositories in bulk would change, checked without enrolling anything.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <see cref="AutostartPlanTests"/> and for the same reason: a test cannot enroll
/// eleven repositories on a developer's machine without leaving something behind, but every
/// decision that would be made is a pure function of the manifest and the files already there.
/// </para>
/// <para>
/// The cases below are the ones a bulk tool gets wrong, and each is a way of quietly destroying
/// something the person did not ask to have touched: another MCP server dropped from a merge, a
/// hand-written peers block replaced by a placeholder, an <c>owns</c> line invented to fill a
/// blank, or eight repositories enrolled and the ninth silently skipped.
/// </para>
/// </remarks>
[Unit]
public sealed class EnrollmentPlanTests
{
    private const string Workspace = "/srv/checkouts/orders";

    private static EnrollmentEntry Entry(
        string agent = "orders",
        string? owns = "Carts and order totals. No tax, no stock.",
        bool unattended = false) =>
        new(Workspace, agent, owns, unattended);

    private static IReadOnlyList<string> Validate(params EnrollmentEntry[] entries) =>
        EnrollmentPlan.Validate(entries, _ => true);

    [Fact]
    public void A_well_formed_owns_line_passes()
    {
        var problems = Validate(
            new EnrollmentEntry(Workspace, "orders", "Carts and totals. No tax.", false));

        Assert.Empty(problems);
    }

    [Fact]
    public void The_facilitators_role_is_refused_and_parley_is_not()
    {
        // Refused here with a sentence, before storage refuses the registration and the agent never appears with
        // nothing said about why. `parley` is an ordinary role: this repository registers as its own agent under it,
        // and a folder called Parley enrols as it (SPEC.md §12.3).
        Assert.Contains(
            Validate(new EnrollmentEntry(Workspace, Facilitation.Role, "", false)),
            p => p.Contains(Facilitation.Reserved, StringComparison.Ordinal));

        Assert.Empty(Validate(new EnrollmentEntry(Workspace, "parley", "", false)));
    }

    [Fact]
    public void The_persons_role_is_refused_and_a_user_is_not()
    {
        // SPEC.md §12.3 says enrolment refuses a repository under the person's seat: otherwise the repository's
        // adapter would attach under the person's name. `user` stays an ordinary role.
        Assert.Contains(
            Validate(new EnrollmentEntry(Workspace, PersonSeat.Role, "", false)),
            p => p.Contains(PersonSeat.Reserved, StringComparison.Ordinal));

        Assert.Empty(Validate(new EnrollmentEntry(Workspace, "user", "", false)));
    }

    [Fact]
    public void An_empty_owns_line_needs_no_approval()
    {
        // Declining to state a boundary is a legitimate answer and must not need a second
        // ceremony: an agent with no line is listed with none and askers fall back to its name.
        Assert.Empty(Validate(new EnrollmentEntry(Workspace, "orders", "", false)));
    }

    [Fact]
    public void An_empty_owns_line_omits_the_key_rather_than_writing_a_placeholder()
    {
        // The refusal this whole command is built around. SPEC.md §17 question 10 says the field
        // is worth routing by because a person meant it; a placeholder left behind by tooling is
        // indistinguishable from a claim somebody made, from the far side of a message.
        var plan = EnrollmentPlan.Describe(Entry(owns: ""), null, null, false);

        var env = JsonNode.Parse(plan.LaunchConfiguration!)!["mcpServers"]!["parley"]!["env"]!;

        Assert.Null(env["PARLEY_OWNS"]);
        Assert.Equal("orders", env["PARLEY_AGENT"]!.GetValue<string>());
        Assert.Contains(plan.Notes, n => n.Contains("no owns line", StringComparison.Ordinal));
    }

    [Fact]
    public void Another_mcp_server_survives_the_merge()
    {
        // A repository may already run other servers, and a setup step that removed them would be
        // met as tooling that stopped working for a reason nobody would connect to Parley.
        const string existing = """
            { "mcpServers": { "sqlite": { "command": "mcp-sqlite" } } }
            """;

        var plan = EnrollmentPlan.Describe(Entry(), existing, null, false);
        var servers = JsonNode.Parse(plan.LaunchConfiguration!)!["mcpServers"]!;

        Assert.Equal("mcp-sqlite", servers["sqlite"]!["command"]!.GetValue<string>());
        Assert.Equal("parley-mcp", servers["parley"]!["command"]!.GetValue<string>());
        Assert.Contains(plan.Notes, n => n.Contains("leaving 1 other server", StringComparison.Ordinal));
    }

    [Fact]
    public void An_existing_peers_section_is_never_rewritten()
    {
        // The block holds three kinds of line and only two belong to any tool. Nothing here can
        // tell a rule the project added from the roster it was added about, and the thing that
        // cannot tell is the thing about to delete it.
        const string instructions = """
            # Orders

            ## Peers

            - `billing` says it owns invoicing. Where a claim conflicts with BOUNDARIES.md, BOUNDARIES wins.
            """;

        var plan = EnrollmentPlan.Describe(Entry(), null, instructions, false);

        Assert.Null(plan.Instructions);
        Assert.Contains(plan.Notes, n => n.Contains("left untouched", StringComparison.Ordinal));
    }

    [Fact]
    public void The_frame_is_appended_to_instructions_that_have_no_peers_section()
    {
        var plan = EnrollmentPlan.Describe(Entry(), null, "# Orders\n\nExisting guidance.\n", false);

        Assert.NotNull(plan.Instructions);
        Assert.StartsWith("# Orders", plan.Instructions, StringComparison.Ordinal);
        Assert.Contains("Existing guidance.", plan.Instructions, StringComparison.Ordinal);
        Assert.Contains("The roster has not been read yet", plan.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void A_created_file_has_one_kind_of_line_ending_whatever_this_source_was_checked_out_with()
    {
        // The frame is a raw string literal, so it carries this file's own line endings — which
        // differ between a clone with autocrlf on and one without. Without normalising, the same
        // command emits a different CLAUDE.md on two machines and neither is wrong.
        var created = EnrollmentPlan.Describe(Entry(), null, null, false).Instructions;

        Assert.NotNull(created);
        Assert.DoesNotContain('\r', created);
    }

    [Fact]
    public void The_appended_frame_matches_the_line_endings_of_the_file_it_joins()
    {
        // Line endings are a trap this repository has been caught by before. Appending an
        // LF block to a CRLF file leaves one mixing both, which the next thing to normalise it
        // renders as a whole-file diff for a twelve-line addition.
        var crlf = EnrollmentPlan.Describe(Entry(), null, "# Orders\r\n\r\nGuidance.\r\n", false);

        Assert.NotNull(crlf.Instructions);
        Assert.DoesNotContain(
            '\n',
            crlf.Instructions.Replace("\r\n", string.Empty, StringComparison.Ordinal));

        var lf = EnrollmentPlan.Describe(Entry(), null, "# Orders\n\nGuidance.\n", false);

        Assert.NotNull(lf.Instructions);
        Assert.DoesNotContain('\r', lf.Instructions);
    }

    [Fact]
    public void A_launch_configuration_of_a_shape_that_cannot_be_merged_is_refused()
    {
        // Third instance of one class of bug in this feature: treating a wrong-typed node as an
        // absent one. `mcpServers` as an array was cast away and replaced, losing two server
        // entries while the run reported a successful enrollment.
        Assert.Throws<InvalidDataException>(
            () => EnrollmentPlan.Describe(Entry(), """{"mcpServers":["sqlite"]}""", null, false));

        Assert.Throws<InvalidDataException>(
            () => EnrollmentPlan.Describe(Entry(), """["not","an","object"]""", null, false));
    }

    [Fact]
    public void The_frame_keeps_the_line_saying_the_inbox_is_pull()
    {
        // The line most easily lost to a paragraph that looks like boilerplate, and the one that
        // separates an agent which collects its messages from one sitting connected beside them.
        var plan = EnrollmentPlan.Describe(Entry(), null, null, false);

        Assert.Contains("pull rather than push", plan.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Enrolling_a_repository_twice_changes_nothing_the_second_time()
    {
        // Re-running against a partly configured set is the ordinary case, not the exception:
        // somebody adds two repositories to a manifest of eleven and runs it again.
        var first = EnrollmentPlan.Describe(Entry(), null, null, false);
        var second = EnrollmentPlan.Describe(Entry(), first.LaunchConfiguration, first.Instructions, false);

        Assert.Null(second.LaunchConfiguration);
        Assert.Null(second.Instructions);
        Assert.Contains(second.Notes, n => n.Contains("already correct", StringComparison.Ordinal));
    }

    [Fact]
    public void The_workspace_is_written_as_an_overridable_default()
    {
        // .mcp.json is usually committed and the path is machine-specific, so the value has to be
        // something a teammate overrides with a variable rather than by editing a shared file.
        var plan = EnrollmentPlan.Describe(Entry(), null, null, false);
        var env = JsonNode.Parse(plan.LaunchConfiguration!)!["mcpServers"]!["parley"]!["env"]!;

        Assert.Equal(
            "${PARLEY_WORKSPACE:-" + Workspace + "}",
            env["PARLEY_WORKSPACE"]!.GetValue<string>());
    }

    [Fact]
    public void Two_repositories_may_not_claim_one_name()
    {
        // Identity is the one thing Parley makes unforgeable, and it does that by taking the name
        // from the launch environment — which is worth nothing if two servers claim one name,
        // because either may then collect the other's mail.
        var problems = Validate(
            new EnrollmentEntry("/srv/a", "orders", "A.", false),
            new EnrollmentEntry("/srv/b", "orders", "B.", false));

        Assert.Contains(problems, p => p.Contains("collect the other's mail", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Orders")]        // uppercase
    [InlineData("9orders")]       // leading digit
    [InlineData("order_book")]    // underscore
    [InlineData("orders ledger")] // space
    [InlineData("")]              // empty
    public void A_malformed_agent_name_is_refused(string name)
    {
        Assert.Contains(Validate(Entry(agent: name)), p => p.Contains("must be lowercase", StringComparison.Ordinal));
    }

    [Fact]
    public void A_name_of_exactly_the_limit_is_accepted()
    {
        // The boundary, because an off-by-one here rejects a legal name and the person has no way
        // to tell that from the tool being wrong about the rule.
        Assert.Empty(Validate(Entry(agent: new string('a', EnrollmentPlan.NameLimit))));
    }

    [Fact]
    public void A_long_owns_line_is_accepted_because_no_bound_is_in_force()
    {
        // There is no bound: an agent asked what it owns answers in two halves and names a peer, which a
        // hundred characters frequently will not hold. What still refuses is the character rule,
        // which the theory below drives -- and that is the half the schema has always cared about.
        Assert.Empty(Validate(Entry(owns: new string('x', 4_000))));
    }

    [Theory]
    [InlineData("Carts and orders.\nNo tax.")]            // a line break
    [InlineData("Carts and orders.\u2028No tax.")]        // a separator wherever rendered
    [InlineData("Carts\u202Eand orders. No tax.")]        // reorders what follows
    [InlineData("Carts and orders.\u00ADNo tax.")]        // invisible
    [InlineData("Carts and orders.\u0000No tax.")]        // hides bytes behind a terminator
    public void An_owns_line_the_schema_would_refuse_is_caught_here(string owns)
    {
        // The store refuses these in a CHECK constraint, so anything let through would come back
        // as a registration rejected at launch — which reads as an agent that simply never
        // appeared, with nothing said about why. Asked of OwnershipStatement rather than checked
        // again, because a second copy of the rule drifts from the one actually enforced.
        Assert.NotEmpty(Validate(Entry(owns: owns)));
    }

    [Fact]
    public void A_workspace_that_is_not_there_is_refused()
    {
        var problems = EnrollmentPlan.Validate([Entry()], _ => false);

        Assert.Contains(problems, p => p.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void A_relative_workspace_is_refused()
    {
        // Only an operator ever sees this value, and it is how `parley registry` tells two
        // similarly named agents apart. A relative path resolves against whatever directory the
        // reader happens to be in, which makes it worse than blank.
        var problems = Validate(new EnrollmentEntry("checkouts/orders", "orders", "A.", false));

        Assert.Contains(problems, p => p.Contains("absolute path", StringComparison.Ordinal));
    }

    [Fact]
    public void One_directory_may_not_be_two_agents()
    {
        // Worse than the duplicate-name case it
        // mirrors: both entries write the same .mcp.json, so the second silently overwrites the
        // first while the run reports "Enrolled 2 repositories". One agent exists, two were
        // promised, and nothing afterwards says which.
        var problems = Validate(
            new EnrollmentEntry("/srv/orders", "first", "One.", false),
            new EnrollmentEntry("/srv/orders", "second", "Two.", false));

        Assert.Contains(problems, p => p.Contains("workspace is also claimed", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_casings_of_one_path_follow_what_the_filesystem_does()
    {
        // Neither answer is right everywhere. On Windows and macOS these name one directory, so
        // the second entry would overwrite the first; on Linux they are two, and refusing them
        // would reject a legitimate manifest. A single hard-coded comparer is a bug on whichever
        // platform it was not written for — and CI runs all three.
        var problems = Validate(
            new EnrollmentEntry("/srv/Orders", "first", "One.", false),
            new EnrollmentEntry("/srv/orders", "second", "Two.", false));

        if (OperatingSystem.IsLinux())
        {
            Assert.Empty(problems);
        }
        else
        {
            Assert.Contains(problems, p => p.Contains("workspace is also claimed", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void An_empty_row_is_named_rather_than_thrown_over()
    {
        // A hand-edited manifest with an object's body deleted and its comma left. This used to
        // reach the writing loop and come back as a NullReferenceException stack trace, which
        // tells somebody editing JSON nothing at all about which row is wrong.
        var problems = EnrollmentPlan.Validate([null, Entry()], _ => true);

        Assert.Contains(problems, p => p.Contains("agents[0]: is empty", StringComparison.Ordinal));
    }

    [Fact]
    public void One_bad_entry_is_reported_alongside_the_rest()
    {
        // Whole-manifest rather than fail-fast, so somebody fixing a manifest of eleven does it
        // in one pass instead of eleven runs.
        // The third entry carries a character the roster rule refuses, since there is no length
        // bound to fault -- and keeping three faults is the point, since what is being measured is that all of them are reported at once.
        var problems = Validate(
            new EnrollmentEntry("/srv/a", "Orders", "A.", false),
            new EnrollmentEntry("relative", "billing", "B.", false),
            new EnrollmentEntry("/srv/c", "stock", "Stock." + (char)0x2028 + "All of it.", false));

        Assert.Equal(3, problems.Count);
    }

    [Fact]
    public void Consent_is_only_recorded_when_the_manifest_asks_for_it()
    {
        // Listing a repository in node.json is the consent, not a description of it: it says a
        // peer's question may start that repository with nobody watching.
        Assert.False(EnrollmentPlan.Describe(Entry(unattended: false), null, null, false).AddsNodeEntry);
        Assert.True(EnrollmentPlan.Describe(Entry(unattended: true), null, null, false).AddsNodeEntry);
    }

    [Fact]
    public void A_repository_already_listed_for_unattended_running_is_not_added_twice()
    {
        var plan = EnrollmentPlan.Describe(Entry(unattended: true), null, null, nodeAlreadyLists: true);

        Assert.False(plan.AddsNodeEntry);
        Assert.Contains(plan.Notes, n => n.Contains("already listed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Acme.Orders.Api", "acme-orders-api")]
    [InlineData("orders", "orders")]
    [InlineData("2024-orders", "orders")]
    [InlineData("...", "agent")]
    public void A_directory_name_becomes_a_well_formed_candidate(string directory, string expected)
    {
        // A starting point rather than an answer — the manifest says so — but it has to at least
        // be a name the registry would accept, or the first run is a wall of validation errors.
        var suggestion = EnrollCommand.SuggestName(directory);

        Assert.Equal(expected, suggestion);
        Assert.True(EnrollmentPlan.IsWellFormedName(suggestion));
    }

    [Fact]
    public void A_very_long_directory_name_is_trimmed_to_something_legal()
    {
        var suggestion = EnrollCommand.SuggestName(new string('a', 80));

        Assert.True(EnrollmentPlan.IsWellFormedName(suggestion));
        Assert.Equal(EnrollmentPlan.NameLimit, suggestion.Length);
    }

    [Fact]
    public void Naming_a_project_writes_it_into_the_launch_environment()
    {
        // PARLEY_PROJECT rather than PARLEY_BROKER, because an address goes stale the first time
        // `parley project set` moves a port and the name does not. The adapter already resolves
        // this variable through the manifest and refuses to guess when it does not know the name.
        var plan = EnrollmentPlan.Describe(Entry(), null, null, false, project: "acme");
        var env = JsonNode.Parse(plan.LaunchConfiguration!)!["mcpServers"]!["parley-acme"]!["env"]!;

        Assert.Equal("acme", env[EnrollmentPlan.ProjectVariable]!.GetValue<string>());
    }

    [Fact]
    public void Naming_no_project_writes_exactly_what_it_wrote_before()
    {
        // An enrolment that names no project writes the `parley` key and no PARLEY_PROJECT.
        var plan = EnrollmentPlan.Describe(Entry(), null, null, false);
        var env = (JsonObject)JsonNode.Parse(plan.LaunchConfiguration!)!["mcpServers"]!["parley"]!["env"]!;

        Assert.False(env.ContainsKey(EnrollmentPlan.ProjectVariable));
    }

    [Fact]
    public void A_repository_that_is_already_another_project_s_agent_gains_a_second_entry()
    {
        // **This test asserted a refusal until 009, and the promise changed deliberately.** The
        // refusal's reasoning was about *re-pointing* — one identity moving between deployments,
        // which does lose an agent from a roster — and it caught a second entry beside the first
        // only because one key could hold one entry. SPEC.md §4.1 has permitted the second
        // throughout: the protocol has no concept of a project, and names collide freely across
        // them. What is still refused is the move; what is admitted is the addition.
        var existing = """
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": { "PARLEY_PROJECT": "elsewhere" } }
              }
            }
            """;

        var plan = EnrollmentPlan.Describe(Entry(), existing, null, false, project: "acme");
        var servers = (JsonObject)JsonNode.Parse(plan.LaunchConfiguration!)!["mcpServers"]!;

        Assert.Equal(
            ["parley", "parley-acme"],
            servers.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));

        // The other deployment's entry is untouched, which is the half the old refusal protected
        // and the half that still matters.
        Assert.Equal(
            "elsewhere",
            servers["parley"]!["env"]![EnrollmentPlan.ProjectVariable]!.GetValue<string>());
    }

    [Fact]
    public void A_repository_already_in_this_project_is_accepted_rather_than_refused()
    {
        var existing = """
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": { "PARLEY_PROJECT": "acme" } }
              }
            }
            """;

        var plan = EnrollmentPlan.Describe(Entry(), existing, null, false, project: "acme");

        Assert.NotNull(plan);
    }

    [Fact]
    public void A_repository_already_pinned_to_a_broker_address_is_refused()
    {
        // Refused when nothing says whether that address is the project being joined: guessing wrong
        // in the permissive direction is the move that loses an agent from a roster.
        var existing = """
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": { "PARLEY_BROKER": "http://127.0.0.1:7777" } }
              }
            }
            """;

        var refusal = Assert.Throws<InvalidDataException>(
            () => EnrollmentPlan.Describe(Entry(), existing, null, false, project: "acme"));

        Assert.Contains("127.0.0.1:7777", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_naming_neither_is_re_pointed_and_said()
    {
        // A change of allegiance in a file that did not record one, so it is said rather than done
        // quietly.
        var existing = """
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } }
              }
            }
            """;

        var plan = EnrollmentPlan.Describe(Entry(), existing, null, false, project: "acme");

        Assert.Contains(plan.Notes, n => n.Contains("becomes an agent of 'acme'", StringComparison.Ordinal));
    }
}
