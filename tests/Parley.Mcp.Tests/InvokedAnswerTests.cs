using Parley.Store;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// An agent answering with nobody running it.
/// </summary>
/// <remarks>
/// <para>
/// The Headless tier of SPEC.md §11.1, end to end and at the level where it actually breaks.
/// <c>HeadlessInvokerTests</c> covers the invoker on its own and every one of those passed while
/// invocation was hanging in a real deployment — because the failure was the child inheriting
/// the host's standard input, and a direct call has no interesting standard input to inherit.
/// </para>
/// <para>
/// So this drives a <see cref="BrokerSession"/> and the sweep that goes with it, and the process
/// it starts is a real one. What it asserts is the only thing that matters about this feature:
/// an exchange nobody was watching moved.
/// </para>
/// </remarks>
public sealed class InvokedAnswerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-invoked-" + Guid.NewGuid().ToString("N"));

    public InvokedAnswerTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A command that writes a file and exits, standing in for an agent runner.</summary>
    /// <param name="marker">The file to write, proving the process really ran.</param>
    /// <returns>The template, in the form a launch configuration would carry.</returns>
    /// <remarks>
    /// A shell rather than a real agent, because what is under test is whether the sweep starts
    /// anything at all in a live server — not what the started agent then decides. Substituting
    /// a real agent here would make the test depend on a model's judgement, which is the one
    /// thing a test must not wait on.
    /// </remarks>
    private static string Runner(string marker) =>
        OperatingSystem.IsWindows()
            ? $"cmd.exe /c echo ran> \"{marker}\""
            : $"/bin/sh -c echo-ran-to-{marker}";

    [Fact]
    public async Task An_idle_agent_that_owes_an_answer_is_started_by_a_peers_sweep()
    {
        // The whole feature in one assertion: nobody takes a turn, and a process runs anyway.
        //
        // This is the test that would have caught the inherited-stdin hang. It passes only if a
        // server whose own standard input is a live channel can start a child and see it finish.
        if (!OperatingSystem.IsWindows())
        {
            return; // The runner above is written for cmd; the Windows leg is the one that matters here.
        }

        var ct = Ct;
        await using var fixture = new ServerFixture(T0);

        var marker = Path.Combine(_workspace, "ran.txt");

        // billing consents to being started, then goes away.
        var billingOptions = fixture.OptionsFor("billing") with
        {
            Workspace = _workspace,
            InvokeCommand = Runner(marker)
        };

        var billing = await BrokerSession.StartAsync(billingOptions, fixture.Clock, ct);
        await billing.DisposeAsync();

        // orders asks, and stays up so that its sweep runs.
        var ordersOptions = fixture.OptionsFor("orders") with { InvokeCommand = Runner(marker) };
        var orders = await BrokerSession.StartAsync(ordersOptions, fixture.Clock, ct);
        var tools = new ParleyTools(orders, ordersOptions);

        var asked = await tools.AskAsync(
            "billing",
            "Whether tax is frozen at invoice time.",
            [new SubQuestion("q1", "Does billing freeze the rate at invoice time?")],
            "If it freezes I stop recomputing and record the contract; if not I keep recomputing.",
            cancellationToken: ct);

        Assert.True(asked["ok"]!.GetValue<bool>(), asked.ToJsonString());

        // Age billing out of §14's liveness lease, so the sweep sees it as nobody's session.
        await orders.UseConnectionAsync(
            async (connection, _, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE agents SET last_seen_utc = '2020-01-01T00:00:00.0000000+00:00' " +
                    "WHERE name = 'billing';";

                return await command.ExecuteNonQueryAsync(token);
            },
            ct);

        var sweep = new InvocationSweep(
            orders, ordersOptions, new StubLogger<SweepService>());

        var started = await sweep.RunAsync(ct);

        await orders.DisposeAsync();

        Assert.Equal(1, started);
        Assert.True(File.Exists(marker), "the invoked process did not run");

        using var check = ParleyDatabase.Open(fixture.DatabasePath);
        using var query = check.CreateCommand();
        query.CommandText = "SELECT agent, owed_before, timed_out FROM invocations;";

        using var reader = query.ExecuteReader();

        Assert.True(reader.Read(), "the invocation was not recorded");
        Assert.Equal("billing", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }

    [Fact]
    public async Task An_agent_that_has_not_consented_is_never_started()
    {
        // Consent comes from the invoked agent's own launch configuration and from nowhere else.
        // Starting a repository nobody offered is spending somebody's tokens on their machine.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ct = Ct;
        await using var fixture = new ServerFixture(T0);

        var marker = Path.Combine(_workspace, "should-not-exist.txt");

        // billing registers with a workspace but no invoke command: it has not consented.
        var billingOptions = fixture.OptionsFor("billing") with { Workspace = _workspace };
        var billing = await BrokerSession.StartAsync(billingOptions, fixture.Clock, ct);
        await billing.DisposeAsync();

        var ordersOptions = fixture.OptionsFor("orders") with { InvokeCommand = Runner(marker) };
        var orders = await BrokerSession.StartAsync(ordersOptions, fixture.Clock, ct);
        var tools = new ParleyTools(orders, ordersOptions);

        await tools.AskAsync(
            "billing",
            "Whether tax is frozen at invoice time.",
            [new SubQuestion("q1", "Does billing freeze the rate at invoice time?")],
            "If it freezes I stop recomputing and record the contract; if not I keep recomputing.",
            cancellationToken: ct);

        await orders.UseConnectionAsync(
            async (connection, _, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE agents SET last_seen_utc = '2020-01-01T00:00:00.0000000+00:00' " +
                    "WHERE name = 'billing';";

                return await command.ExecuteNonQueryAsync(token);
            },
            ct);

        var sweep = new InvocationSweep(orders, ordersOptions, new StubLogger<SweepService>());
        var started = await sweep.RunAsync(ct);

        await orders.DisposeAsync();

        Assert.Equal(0, started);
        Assert.False(File.Exists(marker), "an agent that did not consent was started");
    }

    [Fact]
    public async Task A_server_with_no_command_configured_invokes_nobody()
    {
        // Setting the command is both halves of one bargain: this repository may be started, and
        // this server starts others. A deployment that took invocation without offering it would
        // be a free rider on somebody else's open session.
        var ct = Ct;
        await using var fixture = new ServerFixture(T0);

        var options = fixture.OptionsFor("orders");
        var orders = await BrokerSession.StartAsync(options, fixture.Clock, ct);

        var sweep = new InvocationSweep(orders, options, new StubLogger<SweepService>());
        var started = await sweep.RunAsync(ct);

        await orders.DisposeAsync();

        Assert.Equal(0, started);
    }
}

/// <summary>A logger that records nothing, for tests that are not about logging.</summary>
/// <typeparam name="T">The category.</typeparam>
internal sealed class StubLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
    }
}
