using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What the projects directory accepts, and every way it refuses.
/// </summary>
/// <remarks>
/// <para>
/// The refusals are the point of this suite. A configuration file that is present but not
/// understood must never be treated as absent, and this repository has now met that failure three
/// times: an unparseable node catalogue read as "no agents" and overwritten, a catalogue whose
/// <c>agents</c> was the wrong JSON type cast away and replaced, and — the reason this feature
/// exists — a catalogue resolved from the working directory while the real one sat elsewhere.
/// </para>
/// <para>
/// Each case below reconstructs one of those, rather than asserting the happy path and trusting
/// the guard. That is the constitution's <i>probe, do not reason</i> applied to a file format.
/// </para>
/// <para>
/// <b>019 replaced one file with a directory of them, and this suite was converted rather than
/// rewritten.</b> Where a rule outlived the file it was written against — an unparseable document
/// refused, unknown keys preserved, a write committed by rename — it is repointed at
/// <c>projects/&lt;name&gt;/project.json</c>, because the rule was about a configuration file with
/// no second copy and that is still what this is. Where the behaviour genuinely ended, the test
/// asserts the <b>absence</b>: two projects sharing a store is not refused any more, it is
/// unsayable, and a suite that simply lost those cases would leave the next reader unable to tell
/// a rule that was removed from one nobody ever wrote.
/// </para>
/// </remarks>
public sealed class ProjectManifestTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-manifest-" + Path.GetRandomFileName());

    public ProjectManifestTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Writes one project's configuration by hand, as somebody editing it would.</summary>
    private void Given(string name, string contents)
    {
        var folder = Path.Combine(_root, ProjectFolder.ProjectsDirectoryName, name);

        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, ProjectFolder.ConfigurationFileName), contents);
    }

    /// <summary>A folder with something in it that is not a project's configuration.</summary>
    private void GivenFolder(string name)
    {
        var folder = Path.Combine(_root, ProjectFolder.ProjectsDirectoryName, name);

        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine");
    }

    private string ConfigurationOf(string name) =>
        ProjectFolder.ConfigurationFor(_root, ProjectName.Create(name));

    // **The store is not a parameter any more**, and that is the whole layout in one signature: it
    // is derived from the name, so there is nothing for a caller to supply and nothing for two
    // callers to disagree about.
    private ProjectSettings Project(
        string name = "orders",
        string endpoint = "http://127.0.0.1:51707",
        string? description = null) =>
        new(
            ProjectName.Create(name),
            ProjectFolder.StoreFor(_root, ProjectName.Create(name)),
            endpoint,
            description);

    // ---- location -------------------------------------------------------------------------

    [Fact]
    public void The_default_location_is_absolute_and_is_a_directory()
    {
        // Closes research.md R1, which was verified on Windows only. .NET's mapping of the
        // configuration folder differs across platforms and macOS is the surprising one, so this
        // is asserted rather than assumed — and it runs on all three.
        //
        // It earned its keep on the first Linux run: GetFolderPath returned the empty string, the
        // combine produced the relative "Parley/projects.json", and this is the only line that
        // said so. See DefaultPathFrom.
        //
        // **It answers with the root now rather than with a file in it**, so the assertion that
        // it ends with `projects.json` is inverted: a default that still named the file would be
        // read as a root by everything downstream and would put `projects/` inside it.
        var path = ProjectManifest.DefaultPath;

        Assert.True(Path.IsPathRooted(path), $"'{path}' is not absolute");
        Assert.Contains(ProjectManifest.DirectoryName, path, StringComparison.Ordinal);

        Assert.False(
            path.EndsWith(ProjectManifest.FileName, StringComparison.Ordinal),
            $"'{path}' still names the manifest file, so the root would be inside it");
    }

    [Fact]
    public void A_missing_configuration_directory_falls_back_to_the_home_directory()
    {
        // The case the runtime actually produced. GetFolderPath is documented to return the empty
        // string when it cannot locate a folder, and combining that gives a *relative* path — one
        // that resolves against whatever directory the command was typed in, so two invocations
        // from two directories each find and write a different deployment, and each one looks like
        // the deployment. That is the ambiguity this feature removes, reappearing above everything
        // it fixed.
        var path = ProjectManifest.DefaultPathFrom(
            folder => folder == Environment.SpecialFolder.UserProfile ? "/home/someone" : string.Empty);

        Assert.True(Path.IsPathRooted(path), $"'{path}' is not absolute");
        Assert.Contains("/home/someone", path.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void No_home_at_all_is_refused_rather_than_resolved_against_the_working_directory()
    {
        // With no per-user location there is nowhere a machine-level root could go, and the
        // honest answer is to say so. Inventing a path that happens to be writable is how the
        // working directory gets back in.
        var refused = Assert.Throws<InvalidOperationException>(
            () => ProjectManifest.DefaultPathFrom(_ => string.Empty));

        Assert.Contains(ProjectManifest.PathVariable, refused.Message, StringComparison.Ordinal);
        Assert.Contains("--config", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_path_beats_the_variable_which_beats_the_default()
    {
        Assert.Equal("/given/here", ProjectManifest.Locate("/given/here", _ => "/from/var"));
        Assert.Equal("/from/var", ProjectManifest.Locate(null, _ => "/from/var"));
        Assert.Equal(ProjectManifest.DefaultPath, ProjectManifest.Locate(null, _ => null));
    }

    [Fact]
    public void The_old_spelling_resolves_to_nothing_and_is_refused_when_it_is_read()
    {
        // **This asserted the opposite, and the compatibility it asserted is withdrawn.** `--config` and
        // PARLEY_CONFIG named the manifest file, and a value ending in `projects.json` was resolved to the
        // directory containing it — for at least one release, because the owner's logon entry held that
        // path verbatim.
        //
        // Nothing has been released. The only machine carrying the old layout is the one this was written
        // on, and `reset.ps1` clears it. A rule kept "for one release" on a product with no releases has
        // no expiry.
        //
        // So `Locate` passes it through untouched and the *reader* refuses it, which is the loud version:
        // a path handed back as a root would have had `projects/` looked for inside a file, found nothing,
        // and reported a well-configured machine as having no projects.
        const string old = "/somewhere/Parley/projects.json";

        Assert.Equal(old, ProjectManifest.Locate(old, _ => null));
        Assert.Equal(old, ProjectManifest.Locate(null, _ => old));

        Assert.False(ProjectManifest.TryRead(old, out var projects, out var problem));
        Assert.Empty(projects);

        // Named with what to do about it, at the one moment somebody can act on it.
        Assert.Contains(ProjectManifest.FileName, problem, StringComparison.Ordinal);
        Assert.Contains("reset.ps1", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_root_is_taken_exactly_as_given()
    {
        // The other half: nothing is trimmed, resolved or second-guessed. A root is what was named.
        Assert.Equal("/somewhere/Parley", ProjectManifest.Locate("/somewhere/Parley", _ => null));
    }

    [Fact]
    public void The_old_spelling_is_recognised_by_name_and_never_by_asking_the_filesystem()
    {
        // The rule has to hold on a machine being set up, which has neither the file nor the directory
        // yet — so a version that tested for existence would behave one way before first run and another
        // after, which is a rule nobody can reason about.
        //
        // Probed by naming a path under a directory that does not exist at all: the refusal comes from the
        // name, so it arrives either way.
        var nowhere = Path.Combine(_root, "never-created", ProjectManifest.FileName);

        Assert.False(ProjectManifest.TryRead(nowhere, out _, out var problem));
        Assert.Contains(ProjectManifest.FileName, problem, StringComparison.Ordinal);

        Assert.False(Directory.Exists(Path.Combine(_root, "never-created")));
    }

    // ---- absent is not an error -----------------------------------------------------------

    [Fact]
    public void A_root_with_no_projects_directory_is_not_an_error()
    {
        // A machine that has never had a project is ordinary, not broken. This is what keeps the
        // feature additive: nothing configured, nothing to say.
        Assert.True(ProjectManifest.TryRead(_root, out var projects, out var problem));
        Assert.Empty(projects);
        Assert.Null(problem);
    }

    [Fact]
    public void An_empty_projects_directory_is_valid()
    {
        Directory.CreateDirectory(Path.Combine(_root, ProjectFolder.ProjectsDirectoryName));

        Assert.True(ProjectManifest.TryRead(_root, out var projects, out _));
        Assert.Empty(projects);
    }

    [Fact]
    public void A_folder_with_no_configuration_in_it_is_passed_over_in_silence()
    {
        // **The one bad shape that is not reported**, and the only one. A folder with no
        // `project.json` is half-created, or somebody's scratch directory beside their projects,
        // and naming it would make an ordinary machine look broken — every other refusal in this
        // suite exists because silence was wrong, so this exception is stated rather than assumed.
        GivenFolder("scratch");
        Given("orders", """{ "endpoint": "http://127.0.0.1:51707" }""");

        Assert.True(ProjectManifest.TryRead(_root, out var projects, out var problem), problem);

        Assert.Equal(["orders"], projects.Select(p => p.Name.Value));
        Assert.Null(problem);
    }

    // ---- present but not understood --------------------------------------------------------

    [Fact]
    public void An_unparseable_configuration_is_refused_and_never_read_as_no_projects()
    {
        // The defect this whole suite is named for. Treating this as "no projects configured"
        // and writing over it destroys the only copy of what the operator described.
        Given("orders", """{ "endpoint": "http://x:1", """);

        Assert.True(ProjectManifest.TryRead(_root, out var projects, out var problem));
        Assert.Empty(projects);
        Assert.NotNull(problem);
        Assert.Contains(ConfigurationOf("orders"), problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configuration_that_is_not_an_object_is_refused()
    {
        Given("orders", """["not", "an", "object"]""");

        // Left out and said, never the machine's other projects with it (the combined review gate).
        Assert.True(ProjectManifest.TryRead(_root, out var kept, out var problem));
        Assert.Empty(kept);
        Assert.Contains("does not hold an object", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_folder_with_any_name_is_nobodys_project_and_costs_nothing()
    {
        // Explorer's "New folder" beside a real project. It stopped every command and the broker, and once reported the
        // report outlived the folder: the watchers' stamp counts configurations only, so removing it changed nothing they
        // compare (the combined review gate, lenses 1 and 3). A folder with no configuration is not a project, whatever
        // it is called.
        Given("orders", """{ "endpoint": "http://127.0.0.1:51707" }""");
        Directory.CreateDirectory(Path.Combine(_root, ProjectFolder.ProjectsDirectoryName, "New folder"));

        Assert.True(ProjectManifest.TryRead(_root, out var projects, out var problem));
        Assert.Equal("orders", Assert.Single(projects).Name.Value);
        Assert.Null(problem);
    }

    [Fact]
    public void One_unreadable_project_does_not_take_the_readable_ones_with_it_silently()
    {
        // The refusal names the project that is wrong. It used to name an index into one array in
        // one file; there is no array now, and a person told "projects[1]" would have nothing to
        // count. FR-029: never reported as the machine having no projects.
        Given("orders", """{ "endpoint": "http://127.0.0.1:51707" }""");
        Given("billing", """{ "endpoint": """);

        // Left out and said, and the readable one kept: until the combined review gate this returned nothing at all,
        // which is exactly what the name of this test says must not happen.
        Assert.True(ProjectManifest.TryRead(_root, out var kept, out var problem));
        Assert.Equal("orders", Assert.Single(kept).Name.Value);

        Assert.Contains("billing", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("projects[", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Being_handed_the_old_manifest_file_is_reported_rather_than_read_as_empty()
    {
        // **A caller that composed the path itself rather than asking Locate.** A directory reader
        // handed a file name finds no directories and says nothing — which is this repository's
        // recorded shape for a green-looking answer to a question nobody asked, and here it would
        // read a fully configured machine as having no projects.
        var file = Path.Combine(_root, ProjectManifest.FileName);

        Assert.False(ProjectManifest.TryRead(file, out var projects, out var problem));
        Assert.Empty(projects);
        Assert.Contains("reset.ps1", problem, StringComparison.Ordinal);
    }

    // ---- per-project rules ------------------------------------------------------------------

    [Fact]
    public void A_folder_that_is_not_a_project_name_is_reported_with_the_rule()
    {
        // The folder's name *is* the project's, so a folder that is not named like one is not a
        // project. Reported rather than passed over: it is somebody's mistake in the one directory
        // whose contents are the answer to "what is on this machine", and silence there is how a
        // project goes missing.
        Given("Orders", """{ "endpoint": "http://127.0.0.1:51707" }""");

        // Left out and said, never the machine's other projects with it (the combined review gate).
        Assert.True(ProjectManifest.TryRead(_root, out var kept, out var problem));
        Assert.Empty(kept);
        Assert.Contains("lowercase", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_store_is_derived_from_the_name_rather_than_named_in_the_file()
    {
        // **This asserted that a relative store path was refused.** The rule was that a relative
        // path resolves against whatever directory the caller happened to be in — which is how
        // consent for ten repositories was nearly written to a file nothing reads.
        //
        // There is no path to be relative now: the store is `parley.db` inside the project's own
        // folder, so the class of mistake is gone rather than guarded. Asserted as an absence,
        // because a suite that merely dropped the case would leave the next reader unable to tell
        // a rule that was removed from one nobody wrote.
        Given("orders", """{ "endpoint": "http://127.0.0.1:51707", "store": "orders/parley.db" }""");

        Assert.True(ProjectManifest.TryRead(_root, out var projects, out var problem), problem);

        var project = Assert.Single(projects);

        Assert.Equal(ProjectFolder.StoreFor(_root, ProjectName.Create("orders")), project.Store);
        Assert.True(Path.IsPathRooted(project.Store), $"'{project.Store}' is not absolute");

        // And the value somebody wrote is ignored rather than honoured — a file that could name a
        // store would be a second place the answer lives, and two places disagree.
        Assert.DoesNotContain("orders/parley.db", project.Store, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unparseable_endpoint_is_refused_when_read_not_when_dialled()
    {
        Given("orders", """{ "endpoint": "nonsense" }""");

        // Left out and said, never the machine's other projects with it (the combined review gate).
        Assert.True(ProjectManifest.TryRead(_root, out var kept, out var problem));
        Assert.Empty(kept);
        Assert.Contains("not an absolute address", problem, StringComparison.Ordinal);
    }

    // ---- collisions --------------------------------------------------------------------------

    [Fact]
    public void Two_projects_cannot_share_a_name_because_a_name_is_a_directory_entry()
    {
        // **`Collisions` refused this and no longer does, because it cannot happen.** A project's
        // name is its folder, and a directory holds one entry per name — so the check was deleted
        // rather than kept, and what replaces it is the filesystem. Stronger than a check, which
        // is the form of Principle V this layout offers.
        //
        // Probed rather than reasoned: the reader is asked what it finds when one name is written
        // twice.
        Given("orders", """{ "endpoint": "http://127.0.0.1:51707" }""");
        Given("orders", """{ "endpoint": "http://127.0.0.1:51708" }""");

        Assert.True(ProjectManifest.TryRead(_root, out var projects, out var problem), problem);

        var project = Assert.Single(projects);

        Assert.Equal("orders", project.Name.Value);
        Assert.Equal("http://127.0.0.1:51708", project.Endpoint);
    }

    [Fact]
    public void Two_projects_cannot_share_a_store_because_a_store_is_derived()
    {
        // **Three tests used to guard this one rule**, and each was found the hard way: two
        // projects naming one database, the same database under two spellings (a `..` in one, the
        // other separator in the other), and two casings of one path on a filesystem that folds
        // case. All three are unsayable now — a store is `parley.db` inside a folder named by the
        // project, so distinct names give distinct stores by construction and one name is one
        // folder.
        //
        // What that check defended is still worth stating: two brokers on one SQLite file corrupt
        // it, because WAL mode needs coherent shared memory between every process touching a
        // database. So the property is asserted directly rather than the refusal that used to
        // approximate it.
        var stores = new[] { "orders", "billing", "inventory" }
            .Select(name => ProjectFolder.StoreFor(_root, ProjectName.Create(name)))
            .ToList();

        Assert.Equal(stores.Count, stores.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // And no two of them are the same file reached by two spellings, which is what the
        // canonicalising comparer was added for.
        Assert.Equal(
            stores.Count,
            stores.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Two_projects_may_not_share_an_endpoint()
    {
        // The one collision left, and the one the layout cannot prevent: two projects on one port
        // is a hand edit away, and only one broker can hold a port.
        var problems = ProjectManifest.Collisions(
            [Project("orders", "http://127.0.0.1:51707"),
             Project("billing", "http://127.0.0.1:51707")]);

        var problem = Assert.Single(problems);

        Assert.Contains("share an endpoint", problem, StringComparison.Ordinal);

        // Naming both, rather than resolving in favour of either: whichever this picked would be
        // somebody's deployment silently not starting.
        Assert.Contains("orders", problem, StringComparison.Ordinal);
        Assert.Contains("billing", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shared_endpoint_is_reported_when_the_directory_is_read()
    {
        // At both ends, so a hand-edited folder meets the same rule `project add` applies. This is
        // the check that genuinely lost its single read-validate-write lock: it spans N files now,
        // and nothing holds them all at once.
        Given("orders", """{ "endpoint": "http://127.0.0.1:51707" }""");
        Given("billing", """{ "endpoint": "http://127.0.0.1:51707" }""");

        // Left out and said, never the machine's other projects with it (the combined review gate).
        Assert.True(ProjectManifest.TryRead(_root, out var kept, out var problem));
        Assert.Empty(kept);
        Assert.Contains("share an endpoint", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_projects_on_different_ports_are_left_alone()
    {
        // The other half, because a check that collapsed everything would refuse a perfectly good
        // machine and be found only by whoever had configured one.
        Assert.Empty(ProjectManifest.Collisions(
            [Project("orders", "http://127.0.0.1:51707"),
             Project("billing", "http://127.0.0.1:51708")]));
    }

    // ---- writing ------------------------------------------------------------------------------

    [Fact]
    public void A_written_project_reads_back_the_same()
    {
        ProjectManifest.Write(_root, [Project(description: "the order pipeline")]);

        Assert.True(ProjectManifest.TryRead(_root, out var projects, out var problem), problem);

        var project = Assert.Single(projects);

        Assert.Equal("orders", project.Name.Value);
        Assert.Equal("http://127.0.0.1:51707", project.Endpoint);
        Assert.Equal("the order pipeline", project.Description);
        Assert.Equal(ProjectFolder.StoreFor(_root, ProjectName.Create("orders")), project.Store);
    }

    [Fact]
    public void Writing_refuses_projects_that_cannot_coexist()
    {
        Assert.Throws<InvalidDataException>(() => ProjectManifest.Write(
            _root,
            [Project("orders", "http://127.0.0.1:51707"),
             Project("billing", "http://127.0.0.1:51707")]));
    }

    [Fact]
    public void Writing_creates_the_folders_when_they_are_absent()
    {
        var nested = Path.Combine(_root, "deep", "deeper");

        ProjectManifest.Write(nested, [Project()]);

        Assert.True(File.Exists(
            ProjectFolder.ConfigurationFor(nested, ProjectName.Create("orders"))));
    }

    [Fact]
    public void A_byte_order_mark_and_line_endings_survive_a_rewrite()
    {
        // Editing one project should not produce a diff touching every line of a file somebody
        // reads by hand.
        Given("orders", string.Empty);

        File.WriteAllText(
            ConfigurationOf("orders"),
            "{\r\n  \"endpoint\": \"http://127.0.0.1:51707\"\r\n}\r\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        ProjectManifest.Write(_root, [Project(description: "changed")]);

        var raw = File.ReadAllBytes(ConfigurationOf("orders"));

        Assert.Equal<byte[]>([0xEF, 0xBB, 0xBF], raw[..3]);
        Assert.Contains("\r\n", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_this_version_does_not_know_about_survive_a_write()
    {
        // The manifest's own summary once promised to preserve them and did the opposite: it built
        // a fresh document holding only the keys it knew, so a `schema_version` written by a newer
        // Parley, or a field somebody added by hand, was deleted by the next `project add` — a run
        // that looked entirely successful and quietly discarded what it did not recognise.
        //
        // An older tool editing a newer tool's file is the one version pairing nobody can plan
        // for, which is exactly why it must not eat anything.
        //
        // **019's first draft of ProjectFolder.Write lost this, and the build stayed green.** It
        // was File.WriteAllText over a freshly built object, which is the same mistake reached by
        // the same route one file format later — so the rule is repointed rather than deleted, and
        // this test is what found the regression.
        Given(
            "orders",
            """
            { "schema_version": 7, "written_by": "a newer parley",
              "endpoint": "http://127.0.0.1:51707", "retention_hours": 720 }
            """);

        Assert.True(ProjectManifest.TryRead(_root, out var read, out var problem), problem);

        ProjectManifest.Write(_root, [.. read, Project("billing", "http://127.0.0.1:51708")]);

        var after = System.Text.Json.Nodes.JsonNode
            .Parse(File.ReadAllText(ConfigurationOf("orders")))!.AsObject();

        Assert.Equal(7, (int)after["schema_version"]!);
        Assert.Equal("a newer parley", (string)after["written_by"]!);
        Assert.Equal(720, (int)after["retention_hours"]!);
    }

    [Fact]
    public void Clearing_an_optional_field_actually_clears_it()
    {
        // The other half of preserving a file: the fields this build owns are authoritative, so an
        // optional one that is now absent must go rather than linger. Preserving everything
        // indiscriminately would make an optional field impossible to remove.
        Given(
            "orders",
            """{ "endpoint": "http://127.0.0.1:51707", "description": "the order pipeline" }""");

        Assert.True(ProjectManifest.TryRead(_root, out var read, out _));
        Assert.Equal("the order pipeline", read[0].Description);

        ProjectManifest.Write(_root, [read[0] with { Description = null }]);

        Assert.True(ProjectManifest.TryRead(_root, out var after, out _));
        Assert.Null(after[0].Description);

        Assert.DoesNotContain(
            "\"description\"",
            File.ReadAllText(ConfigurationOf("orders")),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a description\nwith a newline")]
    [InlineData("x")] // padded to over the cap below
    public void What_this_writes_it_can_read_back(string description)
    {
        // The property, rather than another case. Writing checked only the rules about pairs, so a
        // description containing a newline went in as valid JSON and was refused by the very next
        // read — a tool writing a file it cannot read back, which leaves the machine with an
        // unreadable configuration that the rule protecting configurations then keeps unreadable
        // until somebody edits it by hand.
        var text = description.Length == 1
            ? new string('x', ProjectSettings.MaxDescription + 1)
            : description;

        var project = Project() with { Description = text };

        // Either it refuses to write, or what it wrote reads back. Never one without the other.
        try
        {
            ProjectManifest.Write(_root, [project]);
        }
        catch (InvalidDataException)
        {
            return;
        }

        Assert.True(
            ProjectManifest.TryRead(_root, out _, out var problem),
            $"wrote a configuration it then refused to read: {problem}");
    }

    [Fact]
    public void Writing_over_a_file_that_cannot_be_parsed_is_refused()
    {
        // Belt to the braces reading already provides. Callers reach Write only after TryRead has
        // succeeded, but a Write that started fresh over a file it could not understand would
        // destroy the only copy of what a machine was configured for — and a project's endpoint is
        // the only record of where its broker listens.
        Given("orders", "{ \"endpoint\": ");

        Assert.Throws<InvalidDataException>(() => ProjectManifest.Write(_root, [Project()]));
    }

    [Fact]
    public void Writing_leaves_nothing_half_written_beside_the_configuration()
    {
        // The configuration is written to a sibling and moved over it, because nothing holds a
        // second copy: writing in place truncates first, so a process stopped in between leaves a
        // file every reader afterwards refuses — correctly, and permanently. The visible half of
        // that is asserted here, since an orphaned temporary would rot quietly.
        ProjectManifest.Write(_root, [Project()]);

        Assert.Equal(
            [ProjectFolder.ConfigurationFileName],
            Directory.GetFiles(ProjectFolder.For(_root, ProjectName.Create("orders")))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_project_a_write_does_not_name_loses_its_configuration_and_keeps_its_store()
    {
        // **That is what forgetting a project is**, and it is the same act `project remove` has
        // always been: SPEC.md's wording is that it deletes nothing. A folder with no
        // configuration in it is already defined as not a project, so the two agree by
        // construction rather than by a second rule.
        //
        // Destroying a store is `remove --destroy`, a different act behind a different door. A
        // writer that deleted folders would make every caller of this one capable of it.
        ProjectManifest.Write(_root, [Project("orders"), Project("billing", "http://127.0.0.1:51708")]);

        var store = ProjectFolder.StoreFor(_root, ProjectName.Create("billing"));

        File.WriteAllText(store, "pretend this is a database");

        ProjectManifest.Write(_root, [Project("orders")]);

        Assert.True(ProjectManifest.TryRead(_root, out var projects, out var problem), problem);
        Assert.Equal(["orders"], projects.Select(p => p.Name.Value));

        Assert.False(File.Exists(ConfigurationOf("billing")));
        Assert.True(File.Exists(store), "forgetting a project took its store with it");
    }

    // ---- descriptions --------------------------------------------------------------------

    [Fact]
    public void A_description_that_could_forge_the_listing_is_refused_on_read()
    {
        // The listing is what an operator consults to find out what this machine is configured
        // for, and a description is free text rendered straight into it. A newline in one lets a
        // project's entry produce lines that read as another project's entry; an escape sequence
        // lets it produce anything at all. Refused where the file is read, so a hand-edited
        // configuration meets the same rule `project add` applies.
        Given(
            "orders",
            "{ \"endpoint\": \"http://127.0.0.1:51707\", "
            + "\"description\": \"the shop\\u000Aacme  http://127.0.0.1:1  reachable\" }");

        // Left out and said, never the machine's other projects with it (the combined review gate).
        Assert.True(ProjectManifest.TryRead(_root, out var kept, out var problem));
        Assert.Empty(kept);
        Assert.Contains("control character", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_description_long_enough_to_bury_the_paths_is_refused()
    {
        Given(
            "orders",
            "{ \"endpoint\": \"http://127.0.0.1:51707\", \"description\": \""
            + new string('x', ProjectSettings.MaxDescription + 1) + "\" }");

        // Left out and said, never the machine's other projects with it (the combined review gate).
        Assert.True(ProjectManifest.TryRead(_root, out var kept, out var problem));
        Assert.Empty(kept);
        Assert.Contains("description is", problem, StringComparison.Ordinal);
    }

    // ---- concurrency ------------------------------------------------------------------------

    [Fact]
    public async Task A_write_is_not_refused_because_somebody_is_reading()
    {
        // The commit promises that a reader arriving mid-write sees the old file or the new one.
        // It did not keep that promise on Windows -- it stopped the write happening at all.
        // MoveFileEx with MOVEFILE_REPLACE_EXISTING refuses a destination anybody has open,
        // whatever share mode they used, so `parley project add` failed with an access error
        // naming a file nothing else was writing.
        //
        // Latent while the only reader was an MCP server resolving a project at startup. Reachable
        // every day once the console started re-reading per page it renders -- which makes the
        // command the console tells you to run the one this breaks.
        //
        // Measured before it was fixed: eight readers against 300 writes denied 287 of them.
        //
        // **Repointed at the per-project file, which is where the commit now happens.** Both
        // halves -- File.Replace, and readers opening FileShare.Delete -- moved with it, and a
        // conversion that dropped this test would have let either go unnoticed.
        ProjectManifest.Write(_root, [Project()]);

        using var stopping = new CancellationTokenSource();

        var readers = Enumerable.Range(0, 8).Select(reader => Task.Run(() =>
        {
            while (!stopping.IsCancellationRequested)
            {
                ProjectManifest.TryRead(_root, out _, out _);
            }
        }, TestContext.Current.CancellationToken)).ToArray();

        try
        {
            // Enough writes that the old behaviour cannot pass by luck: it failed 95% of them.
            for (var round = 0; round < 60; round++)
            {
                ProjectManifest.Write(
                    _root, [Project(description: round % 2 == 0 ? "one" : "two")]);
            }
        }
        finally
        {
            await stopping.CancelAsync();
            await Task.WhenAll(readers);
        }

        // And the file is intact rather than merely written: a commit that half-succeeded would
        // leave something no reader accepts, which is the failure the temporary file exists for.
        Assert.True(ProjectManifest.TryRead(_root, out var projects, out var problem), problem);
        Assert.Single(projects);

        // Exactly one file in the project's folder, which is stronger than checking for the
        // pending one by name. Replace takes a backup path and is being passed null; a build that
        // started writing one would leave a second file beside the configuration that reads as
        // configuration, and nothing else here would notice.
        Assert.Equal(
            [ProjectFolder.ConfigurationFileName],
            Directory.GetFiles(ProjectFolder.For(_root, ProjectName.Create("orders")))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Two_writers_at_once_cannot_leave_a_configuration_that_no_longer_parses()
    {
        // The pending file used to be a fixed name -- one name, shared by every writer. So two of
        // them did not merely race: writer A truncated the pending file and began writing, and
        // writer B renamed that same path over the top, committing A's fragment. Probed with four
        // writers over fifty rounds each, before the name was made unique:
        //
        //   5 reads returned a document that no longer parsed, and 11 subsequent writes then
        //   refused with "it exists but could not be parsed, so writing over it would discard
        //   it" -- which is correct, and permanent.
        //
        // That is a project's configuration bricked by two commands running at once, by the one
        // route the temporary-file dance did not cover.
        //
        // Asserted on parseability rather than on the writes succeeding. Concurrent writers still
        // collide and still fail loudly -- a sharing violation, or a target briefly absent while
        // it is being replaced -- and those are transient and recoverable. Unparseable is not.
        // A test that demanded no failures at all would be asserting a lock this does not have.
        ProjectManifest.Write(_root, [Project()]);

        var unparseable = new System.Collections.Concurrent.ConcurrentBag<string>();

        var writers = Enumerable.Range(0, 4).Select(writer =>
        {
            var thread = new Thread(() =>
            {
                for (var round = 0; round < 50; round++)
                {
                    try
                    {
                        ProjectManifest.Write(
                            _root, [Project(description: writer % 2 == 0 ? "one" : "two")]);
                    }
                    catch (Exception collision) when (collision is IOException
                                                          or UnauthorizedAccessException)
                    {
                        // Two commands at once. Loud, transient, and not what this is about.
                    }
                    catch (InvalidDataException refused)
                    {
                        // This one is. Write refuses when it cannot parse what is already there,
                        // so reaching here means a writer believed the file was damaged.
                        unparseable.Add(refused.Message);
                    }

                    if (!ProjectManifest.TryRead(_root, out _, out var problem)
                        && problem?.Contains("not valid JSON", StringComparison.Ordinal) == true)
                    {
                        unparseable.Add(problem);
                    }
                }
            })
            { IsBackground = true };

            thread.Start();

            return thread;
        }).ToArray();

        foreach (var writer in writers)
        {
            writer.Join();
        }

        Assert.Empty(unparseable);

        // And it is readable when the dust settles, rather than merely never observed broken.
        Assert.True(ProjectManifest.TryRead(_root, out var projects, out var settled), settled);
        Assert.Single(projects);

        // No pending file survives a collision. The cleanup used to be unreachable from the
        // fallback rename, which orphaned one per failure -- invisible while every writer used
        // the same name and the next write reused it, a growing pile of files that look like
        // configuration once the name became unique.
        //
        // Only this pattern: ReplaceFile leaves its own ~RF*.TMP working files behind when it
        // fails midway, and those are Windows' to clean up rather than this code's.
        Assert.Empty(Directory.GetFiles(
            ProjectFolder.For(_root, ProjectName.Create("orders")), "*.writing"));
    }

    [Fact]
    public async Task Concurrent_changes_do_not_discard_one_another()
    {
        // Adding a project is a read and then a write, and it was neither locked nor atomic. Two
        // commands each read, each appended to what *it* had read, and each wrote -- so the second
        // silently discarded the first. Nothing failed and nothing said so: two successes
        // reported, one project written. On a machine that enrols repositories from a script that
        // is the shape of the loss, arriving when the machine is busiest and being discovered when
        // something later cannot find a deployment nobody remembers removing.
        //
        // **The lock moved inside `projects/` and the rule it keeps did not.** FR-008 gives the
        // root exactly two entries that are not a project, and a file there would be invisible to
        // a reader that lists directories -- so the lock cannot be mistaken for a project whose
        // name is not one. This is what says the move kept the guarantee.
        //
        // Threads rather than the thread pool, for the reason ManifestCurrencyTests records: a
        // writer that must run is a thread, not a work item, and a pool saturated by the rest of
        // the suite would let this pass by never starting.
        ProjectManifest.Write(_root, []);

        const int writers = 8;

        var barrier = new Barrier(writers);
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        var threads = Enumerable.Range(0, writers).Select(index =>
        {
            var thread = new Thread(() =>
            {
                // Released together, so the read-modify-write windows genuinely overlap rather
                // than queueing behind thread creation.
                barrier.SignalAndWait();

                var added = Project($"p{index}", $"http://127.0.0.1:{5400 + index}");

                // Caught, because an unhandled exception on a background thread does not fail
                // this test -- it hangs it, and a ten-minute stall says nothing about what went
                // wrong. Learned twice now: the same shape stalled a broker-shutdown probe on
                // 2026-09-01, and stalled this one when it was first run without the lock.
                try
                {
                    if (!ProjectManifest.TryUpdate(
                            _root, current => current.Append(added).ToArray(), out var problem))
                    {
                        failures.Add(problem ?? "declined");
                    }
                }
                catch (Exception collision)
                {
                    failures.Add($"{collision.GetType().Name}: {collision.Message}");
                }
            })
            { IsBackground = true };

            thread.Start();

            return thread;
        }).ToArray();

        foreach (var thread in threads)
        {
            thread.Join();
        }

        await Task.CompletedTask;

        Assert.Empty(failures);

        // Every one of them, which is the whole claim. Before the lock this settled on one or two.
        Assert.True(ProjectManifest.TryRead(_root, out var projects, out var unreadable), unreadable);

        Assert.Equal(
            Enumerable.Range(0, writers).Select(i => $"p{i}").Order(StringComparer.Ordinal),
            projects.Select(p => p.Name.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_lock_is_inside_the_projects_directory_where_the_reader_cannot_see_it()
    {
        // The other half of the move, asserted directly: the reader lists *directories*, so a file
        // is invisible to it — but a lock at the root would sit beside `node.json` in a place
        // FR-008 says holds exactly two things, and one named `.lock` in `projects/` must never be
        // read as a project whose name is not one.
        Assert.True(ProjectManifest.TryUpdate(_root, _ => [Project()], out var problem), problem);

        Assert.Contains(
            ".lock",
            Directory.GetFiles(ProjectFolder.ProjectsIn(_root)).Select(Path.GetFileName),
            StringComparer.Ordinal);

        Assert.True(ProjectManifest.TryRead(_root, out var projects, out var unreadable), unreadable);
        Assert.Equal(["orders"], projects.Select(p => p.Name.Value));
    }

    [Fact]
    public void The_suite_never_reads_the_configuration_of_the_machine_it_runs_on()
    {
        // The fourth time this repository has met "resolved from somewhere other than where the
        // real one sits", and the first time it was the tests doing it. ToolProcess cleared
        // PARLEY_CONFIG believing that removed the manifest; an empty value means "unset", and
        // unset means the per-user default -- so clearing it guaranteed the tool read the
        // developer's own configuration. Eight tests, none of them about manifests, failed the
        // day a machine running this suite finally named a project.
        //
        // Asserted on the path the tool prints rather than on the variable, because the variable
        // being set proves nothing about what was resolved from it -- which is the entire lesson
        // of the three failures in this class's own remarks.
        var (exitCode, output, whyNot) = ToolProcess.RunFully([], "project", "list");

        Assert.Equal(string.Empty, whyNot);
        Assert.Equal(0, exitCode);

        // Non-vacuous on every machine, including the ones with nothing configured at all: the
        // tool always names the location it would have used, so there is always a path to compare.
        Assert.Contains("No projects configured", output, StringComparison.Ordinal);

        Assert.DoesNotContain(ProjectManifest.DefaultPath, output, StringComparison.Ordinal);
    }
}
