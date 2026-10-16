using Parley.Client;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a repository already answering in one project can be added to another.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported by the owner: it could not be done at all, and a different name did not help.</b>
/// SPEC.md §4.1 permits it — the protocol has no concept of a project, and membership is which
/// endpoint an agent connects to — and 009 built the deployment tooling for it. What stopped it was
/// one word in this surface: the terminal enrolled with <c>Unattended: true</c> always, and FR-013
/// refuses a second unattended start for one workspace. So the second project was refused by a
/// rule that was never about it.
/// </para>
/// <para>
/// <b>018 removed the rule, and three of the tests that were here went with it.</b> They asserted that
/// the second project was told it got no catalogue line, which project held the unattended start, and
/// that the description did not read as a refusal. There is one catalogue per machine keyed by the
/// agent's identity now: every project shares the one entry, so a second project is told nothing special
/// because nothing special happens to it. What replaced them is
/// <c>tests/Parley.Cli.Tests/EnrollingTwiceTests.cs</c>, which asserts the behaviour rather than the
/// sentence that used to explain its absence.
/// </para>
/// <para>
/// <b>A different name could not have helped, and the reasoning is worth keeping.</b> FR-013 was keyed on
/// the workspace, because the hazard is a node starting a client with that workspace as its working
/// directory — two of them is two models editing one checkout, and renaming the agent does not make it
/// two checkouts. That hazard is closed by construction now: residents are keyed by identity and shared
/// across every project one process hosts, so the second ask finds the first one's process.
/// </para>
/// </remarks>
public sealed class SecondProjectTests
{
    [Fact]
    public void An_ordinary_first_enrolment_still_names_both_files()
    {
        // The change must not cost the common case its description.
        var said = Adding.Describe("C:/repo", "C:/store/alpha-node.json", "alpha");

        Assert.Contains(".mcp.json", said, StringComparison.Ordinal);
        Assert.Contains("alpha-node.json", said, StringComparison.Ordinal);
        Assert.Contains(Adding.Starts, said, StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_it_quotes_is_neutralised()
    {
        // **Found by lens 2, and it is 0.18.9's finding in the dialog that was left out of the
        // fix.** That one caught `Removing.Describe` quoting a manifest's endpoint, store and
        // catalogue path raw; `Withdrawing` was given the same treatment at the same time and this
        // one never was. Every value here comes off disk — a workspace path, a catalogue path, a
        // project name from the manifest — and the sentence stands between somebody and a write
        // into a repository that is not the one they are looking at.
        const string Nasty = "al\u001b[2Jpha";

        var described = Adding.Describe(
            "C:/work/or\u001b[2Jders", "C:/store/no\u001b[2Jde.json", Nasty);

        Assert.DoesNotContain('\u001b', described);
    }

    [Fact]
    public async Task A_start_looks_for_the_new_name_in_the_project_it_was_added_to()
    {
        // Reported by the owner with a screenshot: a repository one project already runs was added to another, and the
        // message line said it registered and could be asked now while the agents list beside it did not show it. The
        // check had asked the starting project about the role the repository has there, which was always yes.
        //
        // **The start goes to the project being added to now** (018). It used to be dispatched through whichever
        // project held the consent, because only that project's node could launch the checkout; every project's node
        // reads the same catalogue, so there is no longer another project to go through. The asking half — which is
        // what the defect was — is unchanged, and is what this still holds.
        var beta = new ProjectSettings(ProjectName.Create("beta"), string.Empty, "http://127.0.0.1:2", null);
        var asked = new List<(string Project, string Agent)>();
        var started = new List<(string Project, string Agent)>();

        var said = await Adding.StartAsync(
            (project, agent, _) =>
            {
                started.Add((project.Name.Value, agent.Value));
                return Task.FromResult(ProjectRead<bool>.Read(true));
            },
            (project, agent, _) =>
            {
                asked.Add((project.Name.Value, agent.Value));
                return Task.FromResult(ProjectRead<bool>.Read(project == beta && agent.Role == "inventory"));
            },
            "inventory",
            beta,
            TestContext.Current.CancellationToken);

        Assert.Equal([("beta", "inventory@" + MachineName.Current)], started);
        Assert.NotEmpty(asked);
        Assert.All(asked, one => Assert.Equal(("beta", "inventory@" + MachineName.Current), one));
        Assert.Contains("It can be asked and told now", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_is_sentences_on_one_line_so_the_message_line_draws_no_replacement_characters()
    {
        // The same screenshot: every line break in the add report was drawn as two replacement characters, because the
        // message line makes every control character visible rather than let one start a forged line.
        var report = Reports.Sentences(["Added 'inventory'.", ".mcp.json: create parley-beta entry", "wrote C:/repo/.mcp.json"]);

        Assert.DoesNotContain('\n', report);
        Assert.DoesNotContain('\r', report);
        Assert.Equal("Added 'inventory'. .mcp.json: create parley-beta entry. wrote C:/repo/.mcp.json.", report);
        Assert.DoesNotContain('\uFFFD', Safe.Text(report));
    }

    [Fact]
    public void No_report_that_reaches_the_message_line_is_joined_with_line_breaks()
    {
        // Five screens built their report the same way and the owner met it on one. Held by reading the source, because
        // each report is only drawn after a dialog, a write and a broker, and a check that drove all five would test
        // those rather than this.
        var root = new DirectoryInfo(AppContext.BaseDirectory);

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Parley.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root.FullName, "src", "Parley.Terminal"), "*.cs")
            .Where(path => File.ReadAllText(path).Contains("await done(string.Join(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "joined with line breaks for the message line: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_files_description_keeps_its_own_line_breaks()
    {
        // The same fault in a dialog: Text rather than Lines drew every break in the description as a replacement.
        var described = Files.Describe("alpha", null, [], [], 2);

        Assert.DoesNotContain('\uFFFD', described);
        Assert.Contains(Environment.NewLine, described, StringComparison.Ordinal);
    }
}
