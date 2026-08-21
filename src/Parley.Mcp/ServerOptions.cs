using Parley.Core;

namespace Parley.Mcp;

/// <summary>
/// How this server was launched: which database, and which agent it speaks for.
/// </summary>
/// <param name="DatabasePath">The project's Parley database.</param>
/// <param name="Agent">The agent every operation from this process acts as.</param>
/// <param name="ResponderModel">
/// What to record as having produced a message when a call does not say. See SPEC.md §11.3.
/// </param>
/// <param name="TickInterval">How often the sweep runs.</param>
/// <remarks>
/// <para>
/// <b><see cref="Agent"/> comes from the launch configuration and from nowhere else.</b> It is
/// deliberately not a tool parameter, and that is the single most important decision in this
/// project. An MCP server is started by its client with an environment it does not choose, so
/// the identity is fixed before the model gets a turn — no tool call can change who the caller
/// is, because no tool call mentions it. Had it been an argument, every agent could act as every
/// other, and §13's guarantee that "a sender cannot forge the agent it claims to be" would rest
/// on a model choosing not to.
/// </para>
/// <para>
/// One process per agent per project follows from that, and matches SPEC.md §4.1: a project is
/// a deployment concept, and a broker's connection cannot see another's file. Two agents on one
/// machine are two launches, two databases only if they are two projects, and never two
/// identities inside one process.
/// </para>
/// </remarks>
public sealed record ServerOptions(
    string DatabasePath,
    AgentName Agent,
    string ResponderModel,
    TimeSpan TickInterval)
{
    /// <summary>The environment variable naming the database file.</summary>
    public const string DatabaseVariable = "PARLEY_DB";

    /// <summary>The environment variable naming the agent this server speaks for.</summary>
    public const string AgentVariable = "PARLEY_AGENT";

    /// <summary>The environment variable naming the default responder identity.</summary>
    public const string ResponderVariable = "PARLEY_RESPONDER";

    /// <summary>The environment variable overriding the sweep interval, in seconds.</summary>
    public const string TickVariable = "PARLEY_TICK_SECONDS";

    /// <summary>
    /// How often the sweep runs when the launch configuration does not say.
    /// </summary>
    /// <remarks>
    /// A minute is far below every deadline it enforces — the shortest is §6's fifteen-minute
    /// delivery window — so nothing waits materially longer than it should, and the cost is a
    /// handful of indexed queries against a local file. Frequent and cheap beats rare and
    /// bursty here: a sweep that ran hourly would leave an exchange orphaned for up to an hour
    /// after its window closed, and the asker waiting on a deadline that had already passed.
    /// </remarks>
    public static TimeSpan DefaultTickInterval { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Reads the configuration from the process environment.
    /// </summary>
    /// <returns>The options this server was launched with.</returns>
    /// <exception cref="InvalidOperationException">
    /// A required variable is missing, or one carries a value that cannot be used.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Fails at start-up rather than on the first tool call. A server that launched happily and
    /// then refused everything would report its misconfiguration to the model, which can do
    /// nothing about it, instead of to the person who wrote the launch configuration — and MCP
    /// clients surface a process that exits with a message far better than they surface a tool
    /// that always errors.
    /// </para>
    /// <para>
    /// The database is not created here if it is missing. <see cref="BrokerSession"/> opens and
    /// migrates it, and a path that does not exist yet is the ordinary first run of a project.
    /// </para>
    /// </remarks>
    public static ServerOptions FromEnvironment()
    {
        var databasePath = Require(DatabaseVariable);
        var agentName = Require(AgentVariable);

        if (!AgentName.TryCreate(agentName, out var agent))
        {
            throw new InvalidOperationException(
                $"{AgentVariable} is '{agentName}', which is not a valid agent name. Names are " +
                "lowercase letters, digits and hyphens, starting with a letter, up to 32 " +
                "characters.");
        }

        // Defaulted rather than required, because most deployments have one and the same answer
        // and a variable everybody must set is a variable everybody sets wrong once.
        var responder = Environment.GetEnvironmentVariable(ResponderVariable);

        if (string.IsNullOrWhiteSpace(responder))
        {
            responder = "unknown-model";
        }

        return new ServerOptions(databasePath, agent, responder, ReadTickInterval());
    }

    /// <summary>Reads a required variable.</summary>
    /// <param name="name">The variable's name.</param>
    /// <returns>Its value.</returns>
    /// <exception cref="InvalidOperationException">It is unset or blank.</exception>
    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);

        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                $"{name} is not set. A Parley MCP server is configured by its launch " +
                $"environment: {DatabaseVariable} names the project's database and " +
                $"{AgentVariable} names the agent this process speaks for.")
            : value;
    }

    /// <summary>Reads the sweep interval, or falls back to the default.</summary>
    /// <returns>How often to sweep.</returns>
    /// <exception cref="InvalidOperationException">The value is not a positive number of seconds.</exception>
    private static TimeSpan ReadTickInterval()
    {
        var configured = Environment.GetEnvironmentVariable(TickVariable);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultTickInterval;
        }

        // Refused rather than defaulted quietly. An operator who set this meant something by it,
        // and silently ignoring an unreadable value gives them a deadline machinery running at a
        // cadence they did not choose and cannot see.
        if (!int.TryParse(configured, out var seconds) || seconds <= 0)
        {
            throw new InvalidOperationException(
                $"{TickVariable} is '{configured}', which is not a positive whole number of " +
                "seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
