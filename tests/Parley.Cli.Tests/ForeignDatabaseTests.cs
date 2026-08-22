using System.Globalization;
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
            // Best effort; pooled connections keep the file open.
        }
    }

    private string MakeForeignDatabase()
    {
        var path = Path.Combine(_directory, "app.db");

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
    public void Pointing_it_at_somebody_elses_database_changes_nothing()
    {
        var path = MakeForeignDatabase();

        var (exitCode, error) = ToolProcess.Run("--db", path, "agents");

        Assert.Equal(78, exitCode);
        Assert.Contains("not a Parley database", error, StringComparison.Ordinal);

        SqliteConnection.ClearAllPools();
        var (version, tables) = Inspect(path);

        Assert.Equal(0, version);
        Assert.Equal(1, tables);
    }

    [Fact]
    public void A_path_that_does_not_exist_is_not_created()
    {
        // An operator command against a project that has never run is a mistyped path far more
        // often than a first use, and creating one would answer "no agents registered" to a
        // question about the wrong directory.
        var path = Path.Combine(_directory, "nowhere.db");

        var (exitCode, error) = ToolProcess.Run("--db", path, "agents");

        Assert.Equal(78, exitCode);
        Assert.Contains("created by the first agent server", error, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void The_environment_variable_the_servers_are_launched_with_is_the_one_this_reads()
    {
        // End to end through the real binary, because the name used to be spelled twice — a
        // constant in the MCP server and a literal here — in assemblies that deliberately do not
        // reference each other. Comparing two constants proves they are equal; running the
        // process proves this is the variable it actually reads, which is the part that would
        // have left an operator administering a database the agents were not using.
        var path = MakeForeignDatabase();

        var (exitCode, error) = ToolProcess.Run(
            [(Parley.Core.DeploymentEnvironment.DatabaseVariable, path)], "agents");

        // Refused for being a foreign database, which is only reachable if it opened this path —
        // no --db was given, so the variable is the only way it could have found it.
        Assert.Equal(78, exitCode);
        Assert.Contains("not a Parley database", error, StringComparison.Ordinal);
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
        Assert.Contains("parley [--db <path>] agents", error, StringComparison.Ordinal);
        Assert.DoesNotContain("No database.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_verb_without_a_database_still_says_so()
    {
        // The other side. Naming a command means you know what you want, and then the missing
        // database is the useful thing to be told.
        var (exitCode, error) = ToolProcess.Run("agents");

        Assert.Equal(78, exitCode);
        Assert.Contains("No database.", error, StringComparison.Ordinal);
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
    public void The_database_option_without_a_path_says_which_half_is_missing()
    {
        // `parley --db`, having forgotten the path. It used to fall through to the stray-option
        // refusal and be told that '--db' is not an option this command takes — false, and false
        // in the most confusing direction: it names the one option the command does have and
        // denies having it.
        var (exitCode, error) = ToolProcess.Run("--db");

        Assert.Equal(64, exitCode);
        Assert.Contains("needs a path after it", error, StringComparison.Ordinal);
        Assert.DoesNotContain("is not an option", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_database_option_given_twice_says_so()
    {
        var (exitCode, error) = ToolProcess.Run("--db", "one.db", "--db", "two.db", "agents");

        Assert.Equal(64, exitCode);
        Assert.Contains("was given more than once", error, StringComparison.Ordinal);
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
        Assert.Contains("is not an option", error, StringComparison.Ordinal);
        Assert.DoesNotContain("No messages in", error, StringComparison.Ordinal);
    }
}
