using Parley.Core;

namespace Parley.Mcp;

/// <summary>
/// How this server was launched: which broker to reach, and which agent it speaks for.
/// </summary>
/// <param name="Broker">Where the broker of SPEC.md §4 is listening.</param>
/// <param name="Agent">The agent every operation from this process acts as.</param>
/// <param name="ResponderModel">
/// What to record as having produced a message when a call does not say. See SPEC.md §11.3.
/// </param>
/// <param name="Workspace">
/// The directory this agent works in, recorded in the registry so an operator can tell which
/// repository a name belongs to, or <see langword="null"/> if the launch did not say.
/// </param>
/// <param name="Owns">
/// One line saying what this agent owns, shown to every peer that lists the roster, or
/// <see langword="null"/> if the launch did not say. See <see cref="OwnershipStatement"/>.
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
/// a deployment concept, and membership is which endpoint an agent connects to. Two agents on
/// one machine are two launches, two brokers only if they are two projects, and never two
/// identities inside one process.
/// </para>
/// <para>
/// <b>What is no longer here is the point of the current shape.</b> This record used to carry a
/// database path, a sweep interval, a retention policy and a clarify cap — every one of which
/// describes the deployment rather than the launch, and every one of which each repository could
/// therefore set differently for the same store. They belong to the broker now, which is the one
/// process that owns the deployment, and an adapter cannot express an opinion about them because
/// there is nowhere left to put one.
/// </para>
/// </remarks>
public sealed record ServerOptions(
    string Broker,
    AgentName Agent,
    string ResponderModel,
    string? Workspace,
    string? Owns = null)
{
    /// <summary>The environment variable naming the broker to connect to.</summary>
    /// <remarks>
    /// The name itself lives in <see cref="BrokerEndpoint"/>, because all three processes read
    /// the same variable and none references another. Kept here as well so a launch
    /// configuration's vocabulary is readable from the type that describes a launch.
    /// </remarks>
    public const string BrokerVariable = BrokerEndpoint.Variable;

    /// <summary>The environment variable naming the agent this server speaks for.</summary>
    public const string AgentVariable = "PARLEY_AGENT";

    /// <summary>The environment variable naming the default responder identity.</summary>
    public const string ResponderVariable = "PARLEY_RESPONDER";


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
    /// Nothing here contacts the broker. An address that resolves to nothing is not a
    /// configuration error — the broker may simply not be running yet — and the failure it
    /// produces has its own message and its own remedy. See <see cref="BrokerUnreachableException"/>.
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

        var broker = BrokerEndpoint.Read(read);
        var agentName = Require(read, AgentVariable);

        // A launch configuration names a role; the machine half is detected here and appended.
        //
        // That split is SPEC.md §4.1.1's and it is the reason `.mcp.json` needs no change to
        // work on a second machine: the same committed file gives docs@server-b on one box and
        // docs@server-a on another. It is also the only honest arrangement -- a machine
        // that can be told what it is called can be told the wrong thing, and two boxes both
        // claiming to be server-b is a failure that shows up as messages arriving somewhere nobody
        // expected, with nothing in the registry that looks wrong.
        //
        // A whole name is accepted too, and only when its machine half is this machine. An
        // operator who writes one out is being explicit rather than mistaken; one who writes
        // another machine's name is telling this process to speak for a repository it is not
        // in, which is the impersonation the detection exists to prevent.
        if (!MachineName.TryNameHere(agentName, out var agent)
            && !(AgentName.TryCreate(agentName, out agent)
                 && string.Equals(agent.Machine, MachineName.Current, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"{AgentVariable} is '{agentName}', which is not a valid agent name. Give a " +
                "role -- lowercase letters, digits and hyphens, starting with a letter, up to " +
                $"32 characters -- and this machine ({MachineName.Current}) is appended to it.");
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
        if (Responder.IsReserved(responder))
        {
            throw new InvalidOperationException(
                $"{ResponderVariable} is '{responder}', and the '{Responder.ReservedPrefix}' " +
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
            broker,
            agent,
            responder,
            string.IsNullOrWhiteSpace(workspace) ? null : workspace,
            owns);
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
                $"environment: {AgentVariable} names the agent this process speaks for, and " +
                $"{BrokerVariable} names the broker to reach if it is not the local default.")
            : value;
    }

}
