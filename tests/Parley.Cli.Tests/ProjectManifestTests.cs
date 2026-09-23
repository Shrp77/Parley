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
        string? description = null) =>
        new(ProjectName.Create(name), store, endpoint, description);

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
        var problems = ProjectManifest.Collisions(
            [Project("orders", store: "/Srv/One.db", endpoint: "http://x:1"),
             Project("billing", store: "/srv/one.db", endpoint: "http://x:2")]);

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
        Assert.Equal("billing", projects[1].Name.Value);
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
    public void Clearing_an_optional_field_actually_clears_it()
    {
        // The other half of preserving a row: the fields this build owns are authoritative, so an
        // optional one that is now absent must go rather than linger. Preserving everything
        // indiscriminately would make an optional field impossible to remove.
        //
        // **This was written about `node_config`, which 019 removed.** The field went; the rule did
        // not, and `description` is what it now has to hold — so the test is repointed rather than
        // deleted, because deleting it would take the guarantee with the field it happened to use.
        Given(
            "{ \"projects\": [ { \"name\": \"orders\", \"store\": \"/srv/o.db\", " +
            "\"endpoint\": \"http://x:1\", \"description\": \"the order pipeline\" } ] }");

        Assert.True(ProjectManifest.TryRead(Path_, out var read, out _));
        Assert.Equal("the order pipeline", read[0].Description);

        ProjectManifest.Write(Path_, [read[0] with { Description = null }]);

        Assert.True(ProjectManifest.TryRead(Path_, out var after, out _));
        Assert.Null(after[0].Description);
        Assert.DoesNotContain("description", File.ReadAllText(Path_), StringComparison.Ordinal);
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
    public void Two_projects_sharing_one_catalogue_is_the_ordinary_case()
    {
        // **This asserted the opposite, and went on asserting it after 018 removed the rule** —
        // red in every run since, under a commit that recorded this suite as green. The rule it
        // held was that consent to run unattended is per project *because the catalogue is*, so
        // two projects naming one file meant either could start what the other had agreed to.
        //
        // Both halves of that are gone. There is one catalogue per machine, keyed by identity, so
        // "two projects sharing one" is not a collision but the only arrangement there is — and a
        // name is no longer what the catalogue holds, so one entry cannot mean a different
        // repository in each project. What is left of the reasoning is in `Collisions` itself,
        // which still refuses a shared *store* and a shared endpoint.
        var shared = Path.Combine(_root, "node.json");

        var collisions = ProjectManifest.Collisions(
        [
            Project("orders", "/srv/orders.db", "http://127.0.0.1:51707", shared),
            Project("billing", "/srv/billing.db", "http://127.0.0.1:51708", shared),
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

    [Fact]
    public async Task A_write_is_not_refused_because_somebody_is_reading()
    {
        // The commit above promises that a reader arriving mid-write sees the old file or the new
        // one. It did not keep that promise on Windows -- it stopped the write happening at all.
        // MoveFileEx with MOVEFILE_REPLACE_EXISTING refuses a destination anybody has open,
        // whatever share mode they used, so `parley project add` failed with an access error
        // naming a file nothing else was writing.
        //
        // Latent while the only reader was an MCP server resolving a project at startup. Reachable
        // every day once the console started re-reading the manifest per page it renders -- which
        // makes the command the console tells you to run the one this breaks.
        //
        // Measured before it was fixed: eight readers against 300 writes denied 287 of them.
        ProjectManifest.Write(Path_, [Project()]);

        using var stopping = new CancellationTokenSource();

        var readers = Enumerable.Range(0, 8).Select(reader => Task.Run(() =>
        {
            while (!stopping.IsCancellationRequested)
            {
                ProjectManifest.TryRead(Path_, out _, out _);
            }
        }, TestContext.Current.CancellationToken)).ToArray();

        try
        {
            // Enough writes that the old behaviour cannot pass by luck: it failed 95% of them.
            for (var round = 0; round < 60; round++)
            {
                ProjectManifest.Write(
                    Path_, [Project(round % 2 == 0 ? "orders" : "billing")]);
            }
        }
        finally
        {
            await stopping.CancelAsync();
            await Task.WhenAll(readers);
        }

        // And the file is intact rather than merely written: a commit that half-succeeded would
        // leave something no reader accepts, which is the failure the temporary file exists for.
        Assert.True(ProjectManifest.TryRead(Path_, out var projects, out var problem), problem);
        Assert.Single(projects);

        // Exactly one file, which is stronger than checking for the pending one by name. Replace
        // takes a backup path and is being passed null; a build that started writing one would
        // leave a second file beside the manifest that reads as configuration, and nothing else
        // here would notice.
        Assert.Equal([Path_], Directory.GetFiles(_root));
    }

    [Fact]
    public void Two_writers_at_once_cannot_leave_a_manifest_that_no_longer_parses()
    {
        // The pending file used to be `projects.json.writing` -- one name, shared by every
        // writer. So two of them did not merely race for the manifest: writer A truncated the
        // pending file and began writing, and writer B renamed that same path over the manifest,
        // committing A's fragment. Probed with four writers over fifty rounds each, before the
        // name was made unique:
        //
        //   5 reads returned a manifest that no longer parsed, and 11 subsequent writes then
        //   refused with "it exists but could not be parsed, so writing over it would discard
        //   it" -- which is correct, and permanent.
        //
        // That is the file naming every store on this machine, bricked by two commands running
        // at once, by the one route the temporary-file dance did not cover.
        //
        // Asserted on parseability rather than on the writes succeeding. Concurrent writers still
        // collide and still fail loudly -- a sharing violation, or a target briefly absent while
        // it is being replaced -- and those are transient and recoverable. Unparseable is not.
        // A test that demanded no failures at all would be asserting a lock this does not have.
        ProjectManifest.Write(Path_, [Project()]);

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
                            Path_, [Project(writer % 2 == 0 ? "orders" : "billing")]);
                    }
                    catch (Exception collision) when (collision is IOException
                                                          or UnauthorizedAccessException)
                    {
                        // Two commands at once. Loud, transient, and not what this is about.
                    }
                    catch (InvalidDataException refused)
                    {
                        // This one is. Write refuses when it cannot parse what is already there,
                        // so reaching here means a writer believed the manifest was damaged.
                        unparseable.Add(refused.Message);
                    }

                    if (!ProjectManifest.TryRead(Path_, out _, out var problem)
                        && problem?.Contains("could not be parsed", StringComparison.Ordinal) == true)
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
        Assert.True(ProjectManifest.TryRead(Path_, out var projects, out var settled), settled);
        Assert.Single(projects);

        // No pending file survives a collision. The cleanup used to be unreachable from the
        // fallback rename, which orphaned one per failure -- invisible while every writer used
        // the same name and the next write reused it, a growing pile of files that look like
        // configuration once the name became unique.
        //
        // Only this pattern: ReplaceFile leaves its own ~RF*.TMP working files behind when it
        // fails midway, and those are Windows' to clean up rather than this code's.
        Assert.Empty(Directory.GetFiles(_root, "*.writing"));
    }

    [Fact]
    public async Task Concurrent_changes_do_not_discard_one_another()
    {
        // Adding a project is a read and then a write, and it was neither locked nor atomic. Two
        // commands each read the manifest, each appended to what *it* had read, and each wrote --
        // so the second silently discarded the first. Nothing failed and nothing said so: two
        // successes reported, one project written. On a machine that enrols repositories from a
        // script that is the shape of the loss, arriving when the machine is busiest and being
        // discovered when something later cannot find a deployment nobody remembers removing.
        //
        // Threads rather than the thread pool, for the reason ManifestCurrencyTests records: a
        // writer that must run is a thread, not a work item, and a pool saturated by the rest of
        // the suite would let this pass by never starting.
        ProjectManifest.Write(Path_, []);

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

                var added = Project(name: $"p{index}", store: $"/srv/p{index}.db",
                    endpoint: $"http://127.0.0.1:{5400 + index}");

                // Caught, because an unhandled exception on a background thread does not fail
                // this test -- it hangs it, and a ten-minute stall says nothing about what went
                // wrong. Learned twice now: the same shape stalled a broker-shutdown probe on
                // 2026-09-01, and stalled this one when it was first run without the lock.
                try
                {
                    if (!ProjectManifest.TryUpdate(
                            Path_, current => current.Append(added).ToArray(), out var problem))
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
        Assert.True(ProjectManifest.TryRead(Path_, out var projects, out var unreadable), unreadable);

        Assert.Equal(
            Enumerable.Range(0, writers).Select(i => $"p{i}").Order(StringComparer.Ordinal),
            projects.Select(p => p.Name.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_suite_never_reads_the_manifest_of_the_machine_it_runs_on()
    {
        // The fourth time this repository has met "resolved from somewhere other than where the
        // real one sits", and the first time it was the tests doing it. ToolProcess cleared
        // PARLEY_CONFIG believing that removed the manifest; an empty value means "unset", and
        // unset means the per-user default -- so clearing it guaranteed the tool read the
        // developer's own projects.json. Eight tests, none of them about manifests, failed the
        // day a machine running this suite finally named a project.
        //
        // Asserted on the path the tool prints rather than on the variable, because the variable
        // being set proves nothing about what was resolved from it -- which is the entire lesson
        // of the three failures in this class's own remarks.
        var (exitCode, output, _) = ToolProcess.RunFully([], "project", "list");

        Assert.Equal(0, exitCode);

        // Non-vacuous on every machine, including the ones with no manifest at all: the tool
        // always names the file it would have used, so there is always a path to compare.
        Assert.Contains("No projects configured", output, StringComparison.Ordinal);

        Assert.DoesNotContain(ProjectManifest.DefaultPath, output, StringComparison.Ordinal);
    }
}
