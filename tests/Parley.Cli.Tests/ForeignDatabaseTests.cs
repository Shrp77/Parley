using Parley.Core;
using Parley.Configuration;
﻿using System.Globalization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// A mistyped <c>--db</c>.
/// </summary>
/// <remarks>
/// <para>
/// This tool administers a database an agent's server created, so migrating from it is never the
/// intent — and it used to. Pointed at an application's own SQLite file it wrote Parley's seven
/// tables, their indexes and their triggers into it, moved <c>user_version</c> from zero to one,
/// and then reported "No agents registered." A typo should not install a schema.
/// </para>
/// <para>
/// The real binary, because the property is about what the program does to a file rather than
/// what a method returns — and because the first attempt at testing this by reflection asserted
/// that the assembly references <c>Parley.Store</c>, which is true of every version including
/// the broken one.
/// </para>
/// </remarks>
public sealed class ForeignDatabaseTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-cli-foreign-" + Guid.NewGuid().ToString("N"));

    public ForeignDatabaseTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort. A pooled connection can still hold the file, and the alternative --
            // SqliteConnection.ClearAllPools() -- is process-global and disposes handles
            // that other test classes are using. See BannedSymbols.txt.
        }
    }

    private string MakeForeignDatabase() => MakeForeignDatabaseAt(Path.Combine(_directory, "app.db"));

    /// <summary>Somebody else's SQLite database, at a path this test chooses.</summary>
    /// <remarks>
    /// **Takes the path since 019**, because the only way to put a foreign database where Parley will
    /// open it is to put it where a project's store goes. No flag names one any more.
    /// </remarks>
    private static string MakeForeignDatabaseAt(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT);" +
            "INSERT INTO customers (name) VALUES ('acme');";
        command.ExecuteNonQuery();

        return path;
    }

    private static (long Version, long Tables) Inspect(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        var tables = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        return (version, tables);
    }

    [Fact]
    public void Pointing_the_broker_at_somebody_elses_database_changes_nothing()
    {
        // Through `serve`, because that is the only thing that opens a file. The check moved there
        // with the migration it guards rather than being dropped with the class it used to live in:
        // one process creates every store, so it is the only thing left that can create one in the
        // wrong place.
        //
        // **Reached by putting the foreign file where the project's store goes**, which is the only
        // way in now that no flag names a database — and it is a real way in: somebody copies a file
        // into a project's folder, or restores a backup over one.
        var root = Path.Combine(_directory, "root");
        var name = Parley.Core.ProjectName.Create("probe");

        ProjectManifest.Write(
            root,
            [new ProjectSettings(name, ProjectFolder.StoreFor(root, name), "http://127.0.0.1:0", null)]);

        var path = ProjectFolder.StoreFor(root, name);

        MakeForeignDatabaseAt(path);

        var (exitCode, error) = ToolProcess.Run("serve", "--config", root);

        // **69 rather than 74 since 019.** The one-store path classified "could not open this file" as an
        // I/O error; the projects path classifies "this project would not start" as unavailable, and
        // every project failure now shares that code rather than one code per cause. The substance is
        // the sentence, which is unchanged: Parley says it did not create this database and leaves it
        // exactly as it found it.
        Assert.Equal(69, exitCode);
        Assert.Contains("Parley did not create", error, StringComparison.Ordinal);

        var (version, tables) = Inspect(path);

        Assert.Equal(0, version);
        Assert.Equal(1, tables);
    }

    [Fact]
    public void An_operator_command_cannot_create_a_database_at_all()
    {
        // This used to assert that `parley --db <nowhere> registry` refused rather than creating
        // the file, because an operator command against a project that has never run is a
        // mistyped path far more often than a first use.
        //
        // The guarantee is stronger now and the test says so rather than being deleted: an
        // operator command has no --db to mistype. It reaches the broker over the transport and
        // opens nothing, so there is no path it could create a database at.
        var path = Path.Combine(_directory, "nowhere.db");

        var (exitCode, error) = ToolProcess.Run("--db", path, "registry");

        Assert.Equal(64, exitCode);

        // **Named with what replaced it, rather than "not an option this command takes."** Every
        // deployment on every machine was made with this flag, so it is in scripts and in fingers, and
        // a refusal that pretended never to have heard of it would be true and useless.
        Assert.Contains("--db", error, StringComparison.Ordinal);
        Assert.Contains("parley project add <name>", error, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void The_environment_variable_the_servers_are_launched_with_is_the_one_this_reads()
    {
        // End to end through the real binary, because the name is spelled twice — a constant in the
        // MCP server and one here — in assemblies that deliberately do not reference each other.
        // Comparing two constants proves they are equal; running the process proves this is the
        // variable it actually reads, which is the part that would have left an operator
        // administering a deployment the agents were not using.
        //
        // **`PARLEY_DB` until 019 and `PARLEY_CONFIG` now.** The old variable named one store outside
        // everything; this one names the root, which is the only thing left that says where anything
        // is.
        var root = Path.Combine(_directory, "from-the-variable");
        var name = Parley.Core.ProjectName.Create("probe");

        ProjectManifest.Write(
            root,
            [new ProjectSettings(name, ProjectFolder.StoreFor(root, name), "http://127.0.0.1:0", null)]);

        MakeForeignDatabaseAt(ProjectFolder.StoreFor(root, name));

        var (exitCode, error) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, root)], "serve");

        // Refused for being a foreign database, which is only reachable if it opened that store —
        // nothing was given on the command line, so the variable is the only way it could have found
        // the root it lives under.
        Assert.Equal(69, exitCode);
        Assert.Contains("Parley did not create", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bare_command_says_what_it_can_do()
    {
        // `parley` on its own is somebody asking what this is, and it used to answer "no
        // database" — a configuration error about a thing they had not yet been told existed,
        // with no hint that there was a list of commands behind it. Found by installing the tool
        // and typing its name.
        var (exitCode, error) = ToolProcess.Run();

        Assert.Equal(64, exitCode);
        Assert.Contains("parley registry", error, StringComparison.Ordinal);
        Assert.DoesNotContain("No database.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Serving_without_a_database_says_so()
    {
        // The other side. Naming a command means you know what you want, and then having nothing to
        // serve is the useful thing to be told.
        //
        // **And it has to be told rather than waited through.** `serve` waits when it is given a root
        // explicitly — that is the entry an installer registers, and a project named an hour later must
        // come up without it changing. A person who typed `parley serve` on a machine with nothing on it
        // gets a sentence. Making both cases wait hung the whole suite: 4,265 lines, five orphaned
        // brokers, no failure.
        var (exitCode, error) = ToolProcess.Run("serve");

        Assert.Equal(78, exitCode);
        Assert.Contains("nothing to serve", error, StringComparison.Ordinal);

        // The way on is a project, and it is the only way on now. A refusal naming a one-store option
        // used to send a person to deploy by hand outside the manifest; there is no such option to name.
        Assert.Contains("parley project add", error, StringComparison.Ordinal);
        Assert.DoesNotContain("--db", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Asking_for_the_manual_is_not_a_mistake(string flag)
    {
        // It used to answer "'--help' is not an option this command takes" and then print the
        // help anyway, on standard error, with a usage exit code. The user got what they wanted
        // and was told they were wrong for wanting it.
        var (exitCode, error) = ToolProcess.Run(flag);

        Assert.Equal(0, exitCode);

        // On standard output, because it was asked for. Everywhere else the same text is an
        // answer to a mistake and belongs on standard error, which is what lets a script tell
        // the two apart.
        Assert.Equal("", error);
    }

    [Fact]
    public void The_removed_options_are_named_with_what_replaced_them()
    {
        // **Two tests lived here about the grammar of an option that no longer exists**: `--db` with no
        // path after it, and `--db` given twice. Both were real — the first was found by typing
        // `parley --db` and forgetting the path, and getting told that `--db` is not an option this
        // command takes, which is false in the most confusing direction.
        //
        // What replaces them is the one thing still worth checking: somebody who types either flag from
        // memory or from a script is told what to do instead, rather than met with a refusal that
        // pretends never to have heard of it. Every deployment on every machine was made with these.
        foreach (var gone in new[] { "--db", "-d", "--listen", "-l" })
        {
            var (exitCode, error) = ToolProcess.Run(gone, "somewhere", "serve");

            Assert.Equal(64, exitCode);
            Assert.Contains(gone, error, StringComparison.Ordinal);
            Assert.Contains("parley project add <name>", error, StringComparison.Ordinal);
            Assert.Contains("parley serve", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_flag_in_the_wrong_place_never_becomes_data()
    {
        // `parley export --db` used to reach the export with "--db" as the conversation
        // identifier and run a real query against it. A flag in the wrong place silently
        // becoming data is the worst reading of a mistyped command, because it succeeds.
        //
        // Which branch refuses it is not the property. This one is now caught as a --db with no
        // path after it, which is a better description of what the person typed; what matters is
        // that it is refused and that no query ran.
        var (exitCode, error) = ToolProcess.Run(
            "--db", MakeForeignDatabase(), "export", "--db");

        Assert.Equal(64, exitCode);
        Assert.DoesNotContain("No messages in", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_it_does_not_have_is_refused_rather_than_read_as_data()
    {
        // The stray-option branch itself, which needs a flag the command genuinely does not
        // have — the --db cases above are now caught earlier and more precisely, so without
        // this the branch that catches everything else would be covered by nothing.
        var (exitCode, error) = ToolProcess.Run(
            "--db", MakeForeignDatabase(), "export", "--verbose");

        Assert.Equal(64, exitCode);
        // **Named rather than met with "is not an option this command takes."** That sentence was true
        // and useless: `--db` is what every deployment was made with, so the refusal says what replaced
        // it. The property this test is for is unchanged — a flag in the wrong place never becomes data.
        Assert.Contains("was removed", error, StringComparison.Ordinal);
        Assert.DoesNotContain("No messages in", error, StringComparison.Ordinal);
    }
}
