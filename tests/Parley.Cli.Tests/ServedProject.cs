using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;

namespace Parley.Cli.Tests;

/// <summary>
/// A project whose broker is actually listening.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enrolment refuses a project whose broker does not answer</b>, because adding an agent has to
/// leave it registered and registering is the agent attaching. So a fixture that names an endpoint
/// nothing serves no longer describes a usable deployment, and the probes that used one were
/// testing file writing against a state the command now declines to be in.
/// </para>
/// <para>
/// One helper rather than a copy in each of the three files that needed it. Two copies of a fixture
/// is how they come to differ, and this repository has already lost an afternoon to exactly that
/// with its repository-file helper.
/// </para>
/// <para>
/// It also retires the hardcoded ports those fixtures carried — <c>51707</c> in three files, which
/// is the shape of the flake already recorded against <c>FreePort</c>. The listener picks its own.
/// </para>
/// </remarks>
internal sealed class ServedProject : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    private ServedProject(SqliteConnection connection, LoopbackOperator broker, ProjectSettings settings)
    {
        _connection = connection;
        _broker = broker;
        Settings = settings;
    }

    /// <summary>Gets the project, pointed at the broker that is serving it.</summary>
    public ProjectSettings Settings { get; }

    /// <summary>Starts a broker and describes the project it serves.</summary>
    /// <param name="name">The project's name.</param>
    /// <param name="store">Where the manifest says its store is. Not opened; the broker holds its own.</param>
    /// <param name="catalogue">Its node catalogue, or null.</param>
    /// <returns>The served project.</returns>
    /// <remarks>
    /// The store path in the manifest and the database the broker opened are deliberately not the
    /// same thing: these fixtures assert on files, and an in-memory store keeps the broker out of
    /// the directory they are inspecting.
    /// </remarks>
    public static ServedProject Start(string name, string store, string? catalogue)
    {
        var connection = ParleyDatabase.OpenInMemory();
        var broker = LoopbackOperator.Start(connection);

        return new ServedProject(
            connection,
            broker,
            new ProjectSettings(ProjectName.Create(name), store, broker.Address, null));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync().ConfigureAwait(false);

        _connection.Dispose();
    }
}
