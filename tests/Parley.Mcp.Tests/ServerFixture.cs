using Parley.Core;

namespace Parley.Mcp.Tests;

/// <summary>
/// A clock the tests move by hand.
/// </summary>
/// <remarks>
/// A stub rather than <c>FakeTimeProvider</c>, which would mean a package on the test path for
/// two members. Everything here needs is a settable instant: the server reads the clock and
/// never schedules against it, because the one thing that does — the sweep — is a hosted service
/// the tests drive directly.
/// </remarks>
internal sealed class StubClock(DateTimeOffset now) : TimeProvider
{
    /// <summary>The instant every read returns.</summary>
    public DateTimeOffset Now { get; set; } = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// One temporary database and the sessions opened against it.
/// </summary>
/// <remarks>
/// <para>
/// A real file rather than an in-memory database, and that is the point of the fixture rather
/// than an inconvenience. Two agents on one project share a file, and every question about
/// whether two servers can actually see each other's work is a question about the file: an
/// in-memory database is private to its connection, so two sessions over one would pass every
/// test here while sharing nothing.
/// </para>
/// <para>
/// The directory is removed on dispose. WAL leaves a <c>-wal</c> and a <c>-shm</c> beside the
/// database, so the directory goes rather than the file.
/// </para>
/// </remarks>
internal sealed class ServerFixture : IAsyncDisposable
{
    private readonly List<BrokerSession> _sessions = [];
    private readonly string _directory;

    public ServerFixture(DateTimeOffset start)
    {
        _directory = Path.Combine(Path.GetTempPath(), "parley-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "parley.db");
        Clock = new StubClock(start);
    }

    /// <summary>The shared database every session in this fixture opens.</summary>
    public string DatabasePath { get; }

    /// <summary>The clock every session in this fixture reads.</summary>
    public StubClock Clock { get; }

    /// <summary>Builds the launch configuration for one agent.</summary>
    /// <param name="agent">The agent the server speaks for.</param>
    /// <param name="responder">What to attribute its messages to.</param>
    /// <returns>The options.</returns>
    public ServerOptions OptionsFor(string agent, string responder = "test-model") =>
        new(DatabasePath, AgentName.Create(agent), responder, TimeSpan.FromMinutes(1),
            RetentionPolicy.Default, null);

    /// <summary>Launches a server for an agent, as its client would.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    /// <returns>The started session and the tools bound to it.</returns>
    public async Task<(BrokerSession Session, ParleyTools Tools)> LaunchAsync(
        string agent, CancellationToken cancellationToken)
    {
        var options = OptionsFor(agent);
        var session = await BrokerSession.StartAsync(options, Clock, cancellationToken);

        _sessions.Add(session);

        return (session, new ParleyTools(session, options));
    }

    /// <summary>Closes a session as its client closing the pipe would.</summary>
    /// <param name="session">The session to close.</param>
    /// <returns>A task that completes when it is closed.</returns>
    public async Task ShutDownAsync(BrokerSession session)
    {
        _sessions.Remove(session);
        await session.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file the operating system has not finished releasing. The directory is under the
            // temporary path and named for a GUID, so leaving it is untidy rather than a problem
            // — and failing a passing test on it would be worse.
        }
    }
}
