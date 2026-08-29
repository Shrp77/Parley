using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What the project manifest accepts, and every way it refuses.
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

    private string Path_ => Path.Combine(_root, "projects.json");

    private void Given(string contents) => File.WriteAllText(Path_, contents);

    private static ProjectSettings Project(
        string name = "orders",
        string store = "/srv/orders/parley.db",
        string endpoint = "http://127.0.0.1:51707",
        string? node = "/srv/orders/node.json") =>
        new(ProjectName.Create(name), store, endpoint, node, null);

    // ---- location -------------------------------------------------------------------------

    [Fact]
    public void The_default_location_is_absolute_and_under_the_user_profile()
    {
        // Closes research.md R1, which was verified on Windows only. .NET's mapping of the
        // configuration folder differs across platforms and macOS is the surprising one, so this
        // is asserted rather than assumed — and it runs on all three.
        //
        // It earned its keep on the first Linux run: GetFolderPath returned the empty string, the
        // combine produced the relative "Parley/projects.json", and this is the only line that
        // said so. See DefaultPathFrom.
        var path = ProjectManifest.DefaultPath;

        Assert.True(Path.IsPathRooted(path), $"'{path}' is not absolute");
        Assert.EndsWith(ProjectManifest.FileName, path, StringComparison.Ordinal);
        Assert.Contains(ProjectManifest.DirectoryName, path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_configuration_directory_falls_back_to_the_home_directory()
    {
        // The case the runtime actually produced. GetFolderPath is documented to return the empty
        // string when it cannot locate a folder, and combining that gives a *relative* manifest
        // path — one that resolves against whatever directory the command was typed in, so two
        // invocations from two directories each find and write a different file, and each one
        // looks like the manifest. That is the ambiguity this feature removes, reappearing above
        // everything it fixed.
        var path = ProjectManifest.DefaultPathFrom(
            folder => folder == Environment.SpecialFolder.UserProfile ? "/home/someone" : string.Empty);

        Assert.True(Path.IsPathRooted(path), $"'{path}' is not absolute");
        Assert.Contains("/home/someone", path.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void No_home_at_all_is_refused_rather_than_resolved_against_the_working_directory()
    {
        // With no per-user location there is nowhere a machine-level manifest could go, and the
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
        Assert.Equal("/given/here.json", ProjectManifest.Locate("/given/here.json", _ => "/from/var.json"));
        Assert.Equal("/from/var.json", ProjectManifest.Locate(null, _ => "/from/var.json"));
        Assert.Equal(ProjectManifest.DefaultPath, ProjectManifest.Locate(null, _ => null));
    }

    // ---- absent is not an error -----------------------------------------------------------

    [Fact]
    public void An_absent_manifest_is_not_an_error()
    {
        // A machine that has never had one is ordinary, not broken. This is what keeps the
        // feature additive: no manifest, no change to anything.
        Assert.True(ProjectManifest.TryRead(Path_, out var projects, out var problem));
        Assert.Empty(projects);
        Assert.Null(problem);
    }

    [Fact]
    public void A_manifest_naming_no_projects_is_valid()
    {
        Given("""{ "projects": [] }""");

        Assert.True(ProjectManifest.TryRead(Path_, out var projects, out _));
        Assert.Empty(projects);
    }

    [Fact]
    public void A_manifest_with_no_projects_key_is_valid()
    {
        Given("""{ "_readme": ["nothing here yet"] }""");

        Assert.True(ProjectManifest.TryRead(Path_, out var projects, out _));
        Assert.Empty(projects);
    }

    // ---- present but not understood --------------------------------------------------------

    [Fact]
    public void An_unparseable_manifest_is_refused_and_never_read_as_empty()
    {
        // The defect this whole suite is named for. Treating this as "no projects configured"
        // and writing over it destroys the only copy of what the operator described.
        Given("""{ "projects": [ { "name": "orders", } ] """);

        Assert.False(ProjectManifest.TryRead(Path_, out var projects, out var problem));
        Assert.Empty(projects);
        Assert.Contains(Path_, problem);
    }

    [Fact]
    public void A_manifest_that_is_not_an_object_is_refused()
    {
        Given("""["not", "an", "object"]""");

        Assert.False(ProjectManifest.TryRead(Path_, out _, out var problem));
        Assert.Contains("not a JSON object", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_projects_entry_of_the_wrong_type_is_refused()
    {
        // Well-formed JSON, wrong shape. The same loss wearing valid syntax — and the variant
        // that survived the first fix when this was met in the node catalogue.
        Given("""{ "projects": "orders" }""");

        Assert.False(ProjectManifest.TryRead(Path_, out _, out var problem));
        Assert.Contains("not a list", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_row_is_named_by_index_rather_than_thrown_over()
    {
        Given("""{ "projects": [ null ] }""");

        Assert.False(ProjectManifest.TryRead(Path_, out _, out var problem));
        Assert.Contains("projects[0] is empty", problem, StringComparison.Ordinal);
    }

    // ---- per-project rules ------------------------------------------------------------------

    [Fact]
    public void A_malformed_name_is_refused_with_the_rule()
    {
        Given("""{ "projects": [ { "name": "Orders", "store": "/s.db", "endpoint": "http://x:1" } ] }""");

        Assert.False(ProjectManifest.TryRead(Path_, out _, out var problem));
        Assert.Contains("lowercase", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relative_path_is_refused()
    {
        // The mistake the manifest exists to remove: a relative path resolves against whatever
        // directory the caller happened to be in, which is how consent for ten repositories was
        // nearly written to a file nothing reads.
        Given("""{ "projects": [ { "name": "orders", "store": "orders/parley.db", "endpoint": "http://x:1" } ] }""");

        Assert.False(ProjectManifest.TryRead(Path_, out _, out var problem));
        Assert.Contains("is relative", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unparseable_endpoint_is_refused_when_read_not_when_dialled()
    {
        Given("""{ "projects": [ { "name": "orders", "store": "/s.db", "endpoint": "nonsense" } ] }""");

        Assert.False(ProjectManifest.TryRead(Path_, out _, out var problem));
        Assert.Contains("not an absolute address", problem, StringComparison.Ordinal);
    }

    // ---- collisions --------------------------------------------------------------------------

    [Fact]
    public void Two_projects_may_not_share_a_name()
    {
        var problems = ProjectManifest.Collisions([Project(store: "/a.db", endpoint: "http://x:1"), Project(store: "/b.db", endpoint: "http://x:2")]);

        Assert.Contains(problems, p => p.Contains("share a name", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_projects_may_not_share_a_store()
    {
        // Two brokers on one file breaks the isolation SPEC.md 4.1 rests on — the isolation lives
        // in the storage layer, so sharing the file removes it entirely.
        var problems = ProjectManifest.Collisions(
            [Project("orders", store: "/one.db", endpoint: "http://x:1"),
             Project("billing", store: "/one.db", endpoint: "http://x:2")]);

        Assert.Contains(problems, p => p.Contains("share a store", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_projects_may_not_share_an_endpoint()
    {
        var problems = ProjectManifest.Collisions(
            [Project("orders", store: "/a.db", endpoint: "http://127.0.0.1:51707"),
             Project("billing", store: "/b.db", endpoint: "http://127.0.0.1:51707")]);

        Assert.Contains(problems, p => p.Contains("share an endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_casings_of_one_store_follow_what_the_filesystem_does()
    {
        // Neither answer is right everywhere, and CI runs all three platforms, so the assertion
        // has to follow the platform rather than pick a side.
        // Catalogues given explicitly and distinct, because the helper's default gives both
        // projects the same one — which is now a collision in its own right, and would make this
        // test fail for a reason that has nothing to do with what it is asking about.
        var problems = ProjectManifest.Collisions(
            [Project("orders", store: "/Srv/One.db", endpoint: "http://x:1", node: "/srv/a.json"),
             Project("billing", store: "/srv/one.db", endpoint: "http://x:2", node: "/srv/b.json")]);

        if (OperatingSystem.IsLinux())
        {
            Assert.Empty(problems);
        }
        else
        {
            Assert.Contains(problems, p => p.Contains("share a store", StringComparison.Ordinal));
        }
    }

    // ---- writing ------------------------------------------------------------------------------

    [Fact]
    public void A_written_manifest_reads_back_the_same()
    {
        ProjectManifest.Write(Path_, [Project(), Project("billing", "/srv/b.db", "http://127.0.0.1:51708", null)]);

        Assert.True(ProjectManifest.TryRead(Path_, out var projects, out var problem), problem);
        Assert.Equal(2, projects.Count);
        Assert.Equal("orders", projects[0].Name.Value);
        Assert.Equal("/srv/orders/node.json", projects[0].NodeConfig);
        Assert.Null(projects[1].NodeConfig);
    }

    [Fact]
    public void Writing_refuses_projects_that_cannot_coexist()
    {
        Assert.Throws<InvalidDataException>(() => ProjectManifest.Write(
            Path_,
            [Project("orders", "/one.db", "http://x:1"), Project("billing", "/one.db", "http://x:2")]));
    }

    [Fact]
    public void Writing_creates_the_directory_when_it_is_absent()
    {
        var nested = Path.Combine(_root, "deep", "deeper", "projects.json");

        ProjectManifest.Write(nested, [Project()]);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void A_byte_order_mark_and_line_endings_survive_a_rewrite()
    {
        // Editing one project should not produce a diff touching every line of a file somebody
        // reads by hand.
        File.WriteAllText(
            Path_,
            "{\r\n  \"projects\": []\r\n}\r\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        ProjectManifest.Write(Path_, [Project()]);

        var raw = File.ReadAllBytes(Path_);

        Assert.Equal<byte[]>([0xEF, 0xBB, 0xBF], raw[..3]);
        Assert.Contains("\r\n", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_this_version_does_not_know_about_survive_a_write()
    {
        // This method's own summary promised to preserve them and did the opposite: it built a
        // fresh document holding only `_readme` and `projects`, so a `schema_version` written by a
        // newer Parley, or a field somebody added to one row by hand, was deleted by the next
        // `project add` — a run that looked entirely successful and quietly discarded what it did
        // not recognise.
        //
        // An older tool editing a newer tool's file is the one version pairing nobody can plan
        // for, which is exactly why it must not eat anything.
        Given(
            "{ \"schema_version\": 7, \"written_by\": \"a newer parley\", \"projects\": [ " +
            "{ \"name\": \"orders\", \"store\": \"/srv/o.db\", \"endpoint\": \"http://x:1\", " +
            "\"retention_hours\": 720 } ] }");

        Assert.True(ProjectManifest.TryRead(Path_, out var read, out _));

        ProjectManifest.Write(
            Path_, [.. read, Project("billing", "/srv/b.db", "http://x:2", null)]);

        var after = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path_))!.AsObject();

        Assert.Equal(7, (int)after["schema_version"]!);
        Assert.Equal("a newer parley", (string)after["written_by"]!);

        var orders = after["projects"]!.AsArray()
            .First(p => (string)p!["name"]! == "orders")!.AsObject();

        Assert.Equal(720, (int)orders["retention_hours"]!);
    }

    [Fact]
    public void Clearing_a_projects_catalogue_actually_clears_it()
    {
        // The other half of preserving a row: the fields this build owns are authoritative, so an
        // optional one that is now absent must go rather than linger. Preserving everything
        // indiscriminately would make a node catalogue impossible to remove.
        Given(
            "{ \"projects\": [ { \"name\": \"orders\", \"store\": \"/srv/o.db\", " +
            "\"endpoint\": \"http://x:1\", \"node_config\": \"/srv/node.json\" } ] }");

        Assert.True(ProjectManifest.TryRead(Path_, out var read, out _));
        Assert.Equal("/srv/node.json", read[0].NodeConfig);

        ProjectManifest.Write(Path_, [read[0] with { NodeConfig = null }]);

        Assert.True(ProjectManifest.TryRead(Path_, out var after, out _));
        Assert.Null(after[0].NodeConfig);
        Assert.DoesNotContain("node_config", File.ReadAllText(Path_), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a description\nwith a newline")]
    [InlineData("x")] // padded to over the cap below
    public void What_this_writes_it_can_read_back(string description)
    {
        // The property, rather than another case. Writing checked only the rules about pairs, so a
        // description containing a newline went in as valid JSON and was refused by the very next
        // read — a tool writing a file it cannot read back, which leaves the machine with an
        // unreadable manifest that the rule protecting manifests then keeps unreadable until
        // somebody edits it by hand.
        var text = description.Length == 1
            ? new string('x', ProjectSettings.MaxDescription + 1)
            : description;

        var project = Project() with { Description = text };

        // Either it refuses to write, or what it wrote reads back. Never one without the other.
        try
        {
            ProjectManifest.Write(Path_, [project]);
        }
        catch (InvalidDataException)
        {
            return;
        }

        Assert.True(
            ProjectManifest.TryRead(Path_, out _, out var problem),
            $"wrote a manifest it then refused to read: {problem}");
    }

    [Fact]
    public void Writing_over_a_file_that_cannot_be_parsed_is_refused()
    {
        // Belt to the braces reading already provides. Callers reach Write only after TryRead has
        // succeeded, but a Write that started fresh over a file it could not understand would
        // destroy the only copy of what a machine was configured for.
        Given("{ \"projects\": [ ");

        Assert.Throws<InvalidDataException>(() => ProjectManifest.Write(Path_, [Project()]));
    }

    [Fact]
    public void Writing_leaves_nothing_half_written_beside_the_manifest()
    {
        // The manifest is written to a sibling and moved over it, because it names every store on
        // the machine and nothing holds a second copy: writing in place truncates first, so a
        // process stopped in between leaves a file every reader afterwards refuses — correctly,
        // and permanently. The visible half of that is asserted here, since an orphaned temporary
        // would rot quietly.
        ProjectManifest.Write(Path_, [Project()]);

        Assert.Equal(
            [Path.GetFileName(Path_)],
            Directory.GetFiles(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    // ---- the same file under two spellings ------------------------------------------------

    [Fact]
    public void Two_spellings_of_one_store_are_still_one_store()
    {
        // Found by asking what the duplicate check compares rather than by reading that it exists.
        // It compared the text, so a manifest naming one database twice — once with a `..` in it,
        // once with the other separator, both of which a hand-edited file produces readily —
        // passed. What comes out the far side is two brokers holding one SQLite file, which
        // neither reports: WAL mode needs coherent shared memory between every process touching a
        // database, so the damage lands on the store.
        var direct = Path.Combine(_root, "orders", "parley.db");
        var roundabout = Path.Combine(_root, "orders", "..", "orders", "parley.db");

        var collisions = ProjectManifest.Collisions(
        [
            Project("orders", direct, "http://127.0.0.1:51707", null),
            Project("billing", roundabout, "http://127.0.0.1:51708", null),
        ]);

        Assert.Contains(collisions, c => c.Contains("share a store", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_projects_cannot_share_one_node_catalogue()
    {
        // Consent to run unattended is per project because the catalogue is — and nothing was
        // enforcing the second half of that. A node reads one file and agent names collide freely
        // across projects by design, so two projects pointing at one catalogue means `parley up`
        // for either starts entries the other agreed to, under a name that means a different
        // repository in each.
        //
        // It cannot be caught in storage the way §9.3 prefers, because the catalogue is not in the
        // store: it names how to start things on a machine the broker is not on. So it is caught
        // here, on write and on read alike.
        var shared = Path.Combine(_root, "node.json");

        var collisions = ProjectManifest.Collisions(
        [
            Project("orders", "/srv/orders.db", "http://127.0.0.1:51707", shared),
            Project("billing", "/srv/billing.db", "http://127.0.0.1:51708", shared),
        ]);

        Assert.Contains(collisions, c => c.Contains("share a node catalogue", StringComparison.Ordinal));
    }

    [Fact]
    public void A_project_that_starts_nothing_here_does_not_collide_with_another()
    {
        // Absent is a legitimate state — a project whose agents live on other machines — and two
        // of them must not be read as sharing the catalogue neither has.
        var collisions = ProjectManifest.Collisions(
        [
            Project("orders", "/srv/orders.db", "http://127.0.0.1:51707", null),
            Project("billing", "/srv/billing.db", "http://127.0.0.1:51708", null),
        ]);

        Assert.Empty(collisions);
    }

    [Fact]
    public void Two_genuinely_different_stores_are_left_alone()
    {
        // The other half, because a normalisation that collapsed everything would refuse a
        // perfectly good manifest and be found only by whoever had written one.
        var collisions = ProjectManifest.Collisions(
        [
            Project("orders", Path.Combine(_root, "orders.db"), "http://127.0.0.1:51707", null),
            Project("billing", Path.Combine(_root, "billing.db"), "http://127.0.0.1:51708", null),
        ]);

        Assert.Empty(collisions);
    }

    [Fact]
    public void A_description_that_could_forge_the_listing_is_refused_on_read()
    {
        // The listing is what an operator consults to find out what this machine is configured
        // for, and a description is free text rendered straight into it. A newline in one lets a
        // project's entry produce lines that read as another project's entry; an escape sequence
        // lets it produce anything at all. Refused where the file is read, so a hand-edited
        // manifest meets the same rule `project add` applies.
        Given(
            "{ \"projects\": [ { \"name\": \"orders\", \"store\": \"/srv/o.db\", " +
            "\"endpoint\": \"http://x:1\", " +
            "\"description\": \"the shop\\u000Aacme  http://127.0.0.1:1  reachable\" } ] }");

        Assert.False(ProjectManifest.TryRead(Path_, out _, out var problem));
        Assert.Contains("control character", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_description_long_enough_to_bury_the_paths_is_refused()
    {
        Given(
            "{ \"projects\": [ { \"name\": \"orders\", \"store\": \"/srv/o.db\", " +
            "\"endpoint\": \"http://x:1\", \"description\": \"" +
            new string('x', ProjectSettings.MaxDescription + 1) + "\" } ] }");

        Assert.False(ProjectManifest.TryRead(Path_, out _, out var problem));
        Assert.Contains("description is", problem, StringComparison.Ordinal);
    }
}
