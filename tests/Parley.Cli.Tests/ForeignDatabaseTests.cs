using System.Diagnostics;
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

    private static (string FileName, string? Argument) Tool()
    {
        var directory = Path.GetDirectoryName(typeof(ForeignDatabaseTests).Assembly.Location)!;
        var apphost = Path.Combine(directory, OperatingSystem.IsWindows() ? "parley.exe" : "parley");

        if (File.Exists(apphost))
        {
            return (apphost, null);
        }

        var library = Path.Combine(directory, "parley.dll");
        Assert.True(File.Exists(library), $"expected the tool beside the tests in {directory}");

        return ("dotnet", library);
    }

    private static (int ExitCode, string Error) Run(params string[] arguments)
    {
        var (fileName, argument) = Tool();
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (argument is not null)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var value in arguments)
        {
            start.ArgumentList.Add(value);
        }

        // Cleared so an operator's own environment cannot decide which database the test reads.
        start.Environment["PARLEY_DB"] = "";

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "the tool never exited");

        return (process.ExitCode, error);
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

        var (exitCode, error) = Run("--db", path, "agents");

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

        var (exitCode, error) = Run("--db", path, "agents");

        Assert.Equal(78, exitCode);
        Assert.Contains("created by the first agent server", error, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void An_option_it_does_not_have_is_refused_rather_than_read_as_data()
    {
        // `parley export --db` used to reach the export with "--db" as the conversation
        // identifier and run a real query against it. A flag in the wrong place silently
        // becoming data is the worst reading of a mistyped command, because it succeeds.
        var (exitCode, error) = Run("--db", MakeForeignDatabase(), "export", "--db");

        Assert.Equal(64, exitCode);
        Assert.Contains("is not an option", error, StringComparison.Ordinal);
    }
}
