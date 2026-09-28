namespace Parley.Setup;

/// <summary>What an enrolment did, as facts rather than as the text it printed.</summary>
/// <remarks>
/// <para>
/// <b>Here so a surface can say it in one line.</b> The terminal ran the command in process and reprinted
/// everything it wrote for a shell -- the catalogue path, the consent paragraph, a line for every file in
/// every repository -- into a message line one row tall. The owner's call of 2026-09-27: that is noise
/// nobody reads. A shell still gets the whole account; a screen gets these and says what matters.
/// </para>
/// <para>
/// <b>Filled in by the command, never parsed out of its text.</b> Reading the printed report back would
/// make every sentence in it part of a contract nobody would know they were keeping.
/// </para>
/// </remarks>
/// <param name="progress">
/// Told, per agent, what is happening to it while it happens, or null: the role, then a few words such as
/// <c>starting</c> or <c>registering (7s of 30s)</c>. Starting and registering each agent can take the better
/// part of a minute, one after another, and a screen that says nothing for that long is one somebody assumes
/// has hung -- the owner's report of 2026-09-27, after enrolling four. <b>Per agent rather than as a
/// sentence</b> so a surface can show it on the agent's own row, which is where the owner asked for it on
/// 2026-09-28, rather than in a status line under everything else.
/// </param>
public sealed class EnrolmentOutcome(Action<string, string>? progress = null)
{
    private readonly List<string> _enrolled = [];
    private readonly List<string> _registered = [];
    private readonly List<NotStarted> _unregistered = [];

    /// <summary>The roles written into their repositories and catalogued, in the order they were done.</summary>
    public IReadOnlyList<string> EnrolledAgents => _enrolled;

    /// <summary>The roles that were started and registered themselves.</summary>
    public IReadOnlyList<string> RegisteredAgents => _registered;

    /// <summary>The roles that were enrolled and did not register, each with why.</summary>
    public IReadOnlyList<NotStarted> Unregistered => _unregistered;

    /// <summary>What is said of an agent the moment its start is asked for.</summary>
    public const string Starting = "starting";

    /// <summary>What is said of an agent once it has registered.</summary>
    public const string Running = "running";

    /// <summary>What is said of an agent that was started and had not registered when the wait ran out.</summary>
    public const string NotYet = "not registered yet";

    /// <summary>Says what is happening to one agent now.</summary>
    /// <param name="role">The agent's role.</param>
    /// <param name="activity">A few words.</param>
    internal void Say(string role, string activity) => progress?.Invoke(role, activity);

    /// <summary>Records a role as enrolled.</summary>
    /// <param name="role">The role.</param>
    internal void Enrolled(string role) => _enrolled.Add(role);

    /// <summary>Records a role as registered.</summary>
    /// <param name="role">The role.</param>
    internal void Registered(string role) => _registered.Add(role);

    /// <summary>Records a role that did not register.</summary>
    /// <param name="role">The role.</param>
    /// <param name="why">What was said about it.</param>
    internal void NotRegistered(string role, string why) => _unregistered.Add(new NotStarted(role, why));
}

/// <summary>A role that was enrolled and did not register.</summary>
/// <param name="Role">The role.</param>
/// <param name="Why">What was said about it.</param>
public sealed record NotStarted(string Role, string Why);
