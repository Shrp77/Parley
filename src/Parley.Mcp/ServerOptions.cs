using System.Globalization;
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
/// <param name="Retention">How long the message log keeps what it carried. See SPEC.md §9.4.</param>
/// <param name="Workspace">
/// The directory this agent works in, recorded in the registry so an operator can tell which
/// repository a name belongs to, or <see langword="null"/> if the launch did not say.
/// </param>
/// <param name="Owns">
/// One line saying what this agent owns, shown to every peer that lists the roster, or
/// <see langword="null"/> if the launch did not say. See <see cref="OwnershipStatement"/>.
/// </param>
/// <param name="InvokeCommand">
/// The command that starts an agent when it owes an answer and nobody is running it, or
/// <see langword="null"/> to take no part in that. Two things at once: setting it is how this
/// repository consents to being started, and it is what the sweep runs for <em>other</em> idle
/// agents. See SPEC.md §11.1's Headless tier.
/// </param>
/// <param name="ClarifyDepthCap">
/// How many clarifications an exchange opened by this server admits before the broker forces an
/// <c>unknown</c>. Stamped onto each exchange at admission, so a change applies to questions
/// asked after it and not to any already in flight. See SPEC.md §5.2 and §17 question 4.
/// </param>
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
    TimeSpan TickInterval,
    RetentionPolicy Retention,
    string? Workspace,
    string? Owns = null,
    int ClarifyDepthCap = ServerOptions.DefaultClarifyDepthCap,
    string? InvokeCommand = null)
{
    /// <summary>The environment variable naming the database file.</summary>
    /// <remarks>
    /// The name itself lives in <see cref="DeploymentEnvironment"/>, because the operator's tool
    /// reads the same variable and does not reference this assembly. Kept here as well so a
    /// launch configuration's vocabulary is readable from the type that describes a launch.
    /// </remarks>
    public const string DatabaseVariable = DeploymentEnvironment.DatabaseVariable;

    /// <summary>The environment variable naming the agent this server speaks for.</summary>
    public const string AgentVariable = "PARLEY_AGENT";

    /// <summary>The environment variable naming the default responder identity.</summary>
    public const string ResponderVariable = "PARLEY_RESPONDER";

    /// <summary>The environment variable overriding the sweep interval, in seconds.</summary>
    public const string TickVariable = "PARLEY_TICK_SECONDS";

    /// <summary>
    /// The environment variable overriding how many clarifications an exchange admits.
    /// </summary>
    /// <remarks>
    /// Exists so the number can be measured rather than argued. SPEC.md §17 question 4 asks whether a
    /// clarify cap is needed at all, and a cap censors the evidence that would answer it: depths
    /// pile up at whatever the cap is, whether the true need was one more or forty more. Setting
    /// it far above any plausible need for a while is the only way to read the distribution
    /// agents actually produce. The column has always been per exchange, which is what makes
    /// this a launch setting rather than a schema change.
    /// </remarks>
    public const string ClarifyCapVariable = "PARLEY_CLARIFY_CAP";

    /// <summary>
    /// How many clarifications an exchange admits when the launch configuration does not say.
    /// </summary>
    /// <remarks>
    /// Held equal to <see cref="ExchangePolicy.Default"/>'s value by a test rather than by
    /// reference, because a positional record's default must be a compile-time constant. The
    /// test is what stops the two drifting; the duplication is not the risk, an unnoticed
    /// divergence between the documented default and the applied one is.
    /// <para>
    /// 200 rather than 3 since the measurement §17 question 4 asks for became the default
    /// rather than something each deployment had to know to opt into. The consequence is on
    /// <see cref="ExchangePolicy.Default"/>, where the number is applied; read it there before
    /// raising this further.
    /// </para>
    /// </remarks>
    public const int DefaultClarifyDepthCap = 200;

    /// <summary>
    /// The environment variable holding the command that starts an idle agent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the launch environment and offered by no operation, like every other fact about
    /// an agent's identity. Setting it means two things that are deliberately the same setting:
    /// this repository may be started when it owes an answer, and this server will start other
    /// idle agents that have said the same. Splitting them would let a deployment take invocation
    /// without offering it, which is a free rider on somebody else's open session.
    /// </para>
    /// <para>
    /// For a Claude Code agent this is <c>claude -p</c>; the prompt is appended by the invoker,
    /// not written here, so no template can place it where it would be read as a flag. Unset
    /// means this agent neither invokes nor is invoked, which is the state every deployment
    /// starts in.
    /// </para>
    /// </remarks>
    public const string InvokeCommandVariable = "PARLEY_INVOKE_COMMAND";

    /// <summary>The largest value <see cref="ClarifyCapVariable"/> may carry.</summary>
    /// <remarks>
    /// The schema's band, restated so the refusal happens at start-up rather than on the first
    /// question. SPEC.md §9.3 bounds the column by trigger; a server launched above the bound
    /// would start cleanly and then fail to admit any exchange at all, reporting a launch
    /// configuration mistake to the model rather than to whoever made it.
    /// </remarks>
    public const int MaxClarifyDepthCap = 1000;

    /// <summary>
    /// What a message is attributed to when the launch configuration names nothing.
    /// </summary>
    /// <remarks>
    /// SPEC.md §11.3 makes attribution a MUST on every message, so silence needs a value rather
    /// than an omission — and one that says plainly that nobody claimed anything, since the
    /// point of the field is telling a weak responder from a strong one.
    /// </remarks>
    public const string DefaultResponder = "unknown-model";

    /// <summary>The environment variable naming the directory this agent works in.</summary>
    /// <remarks>
    /// Optional, and deliberately not defaulted to the current directory. An MCP client chooses
    /// the working directory of the process it launches, and several do not choose the project
    /// — so a default would record something plausible and wrong in the one field an operator
    /// uses to tell two similarly named agents apart. Unset means unknown, which is true.
    /// </remarks>
    public const string WorkspaceVariable = "PARLEY_WORKSPACE";

    /// <summary>The environment variable saying what this agent owns.</summary>
    /// <remarks>
    /// Set here rather than offered as a tool parameter, and that is the whole argument for
    /// trusting it. The objection to recording what an agent owns in the registry is that a
    /// self-declared field is worth no more than the prose it replaces — true, if a model
    /// declares it. Read from the launch environment it is declared by the person who wrote the
    /// launch configuration, in the same file and by the same mechanism that fixes the agent's
    /// name, and no operation anywhere accepts one. See SPEC.md §17, the tenth question.
    /// </remarks>
    public const string OwnsVariable = DeploymentEnvironment.OwnsVariable;

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
    public static TimeSpan DefaultTickInterval { get; } = TimeSpan.FromSeconds(5);

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
    public static ServerOptions FromEnvironment() => Read(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Reads the configuration from an arbitrary source of variables.
    /// </summary>
    /// <param name="read">Returns a variable's value, or <see langword="null"/> if it is unset.</param>
    /// <returns>The options this server was launched with.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required variable is missing, or one carries a value that cannot be used.
    /// </exception>
    /// <remarks>
    /// Separated from <see cref="FromEnvironment"/> so that the parsing can be tested without
    /// mutating the process environment. That is not a cosmetic concern: the test host runs
    /// classes in parallel, so a test that set <c>PARLEY_AGENT</c> would be setting it for every
    /// other test running at that moment, and the failure would be intermittent and blamed on
    /// something else.
    /// </remarks>
    public static ServerOptions Read(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var databasePath = Require(read, DatabaseVariable);
        var agentName = Require(read, AgentVariable);

        if (!AgentName.TryCreate(agentName, out var agent))
        {
            throw new InvalidOperationException(
                $"{AgentVariable} is '{agentName}', which is not a valid agent name. Names are " +
                "lowercase letters, digits and hyphens, starting with a letter, up to 32 " +
                "characters.");
        }

        // Defaulted rather than required, because most deployments have one and the same answer
        // and a variable everybody must set is a variable everybody sets wrong once.
        var responder = read(ResponderVariable);

        if (string.IsNullOrWhiteSpace(responder))
        {
            responder = DefaultResponder;
        }

        // Refused at start-up, because the broker refuses it on every send. SPEC.md §11.3
        // reserves this namespace and makes refusing an adapter that claims it a MUST, so a
        // server configured this way launches cleanly and then fails every single operation —
        // which is the failure mode this whole method exists to avoid, reported to the model
        // rather than to whoever wrote the launch configuration.
        if (responder.StartsWith(Parley.Broker.Broker.ReservedResponderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ResponderVariable} is '{responder}', and the '{Parley.Broker.Broker.ReservedResponderPrefix}' " +
                "namespace is reserved for the broker's own attributions. See SPEC.md §11.3: an " +
                "adapter claiming it could forge a broker-authored close, so every message this " +
                "server sent would be refused.");
        }

        var workspace = read(WorkspaceVariable);
        var owns = OwnershipStatement.Read(read(OwnsVariable));

        // Refused at start-up for the same reason a malformed agent name is: the constraint that
        // actually enforces this lives in the schema, so a server launched with a bad statement
        // would start cleanly and then fail its own registration — reporting a configuration
        // mistake to the model, which can do nothing about it, rather than to whoever made it.
        if (!OwnershipStatement.IsValid(owns, out var invalid))
        {
            throw new InvalidOperationException($"{OwnsVariable} cannot be used: {invalid}");
        }

        return new ServerOptions(
            databasePath,
            agent,
            responder,
            ReadTickInterval(read),
            // Read from the same names the operator's tool reads, through the same code, so the
            // sweep this server runs and a `parley prune` cannot silently apply different
            // numbers to the same file. See RetentionPolicyEnvironment.
            RetentionPolicyEnvironment.Read(read),
            string.IsNullOrWhiteSpace(workspace) ? null : workspace,
            owns,
            ReadClarifyDepthCap(read),
            Blank(read(InvokeCommandVariable)));
    }

    /// <summary>Reads the clarify cap, or falls back to the default.</summary>
    /// <param name="read">The source of variables.</param>
    /// <returns>How many clarifications an exchange opened by this server admits.</returns>
    /// <exception cref="InvalidOperationException">
    /// The value is not a whole number within the band SPEC.md §9.3 bounds the column to.
    /// </exception>
    private static int ReadClarifyDepthCap(Func<string, string?> read)
    {
        var configured = read(ClarifyCapVariable);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultClarifyDepthCap;
        }

        // Zero is admitted, and is not the same as unset. It means an answerer must answer with
        // what it has, which is a policy somebody may want and which the schema permits. Reading
        // it as "no value" would silently turn the strictest setting into the default one.
        if (!int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cap)
            || cap < 0
            || cap > MaxClarifyDepthCap)
        {
            throw new InvalidOperationException(
                $"{ClarifyCapVariable} is '{configured}', which is not a whole number between 0 " +
                $"and {MaxClarifyDepthCap.ToString(CultureInfo.InvariantCulture)}. The column is " +
                "bounded in the schema (SPEC.md §9.3), so a value outside that band would be " +
                "refused on every exchange this server tried to open.");
        }

        return cap;
    }

    /// <summary>Normalises an optional variable, treating whitespace as absent.</summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The value, or <see langword="null"/> if there was nothing in it.</returns>
    /// <remarks>
    /// A key declared with an empty value is a launch configuration in the middle of being
    /// edited, not a request to run the empty string as a command.
    /// </remarks>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Reads a required variable.</summary>
    /// <param name="read">The source of variables.</param>
    /// <param name="name">The variable's name.</param>
    /// <returns>Its value.</returns>
    /// <exception cref="InvalidOperationException">It is unset or blank.</exception>
    private static string Require(Func<string, string?> read, string name)
    {
        var value = read(name);

        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                $"{name} is not set. A Parley MCP server is configured by its launch " +
                $"environment: {DatabaseVariable} names the project's database and " +
                $"{AgentVariable} names the agent this process speaks for.")
            : value;
    }

    /// <summary>Reads the sweep interval, or falls back to the default.</summary>
    /// <param name="read">The source of variables.</param>
    /// <returns>How often to sweep.</returns>
    /// <exception cref="InvalidOperationException">The value is not a positive number of seconds.</exception>
    private static TimeSpan ReadTickInterval(Func<string, string?> read)
    {
        var configured = read(TickVariable);

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
