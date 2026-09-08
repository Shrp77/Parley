using System.Runtime.Versioning;

namespace Parley.Setup;

/// <summary>
/// The Windows Task Scheduler, reached through its own API rather than through
/// <c>schtasks.exe</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the two are not equivalent, and the difference decides whether the
/// feature works at all.</b> <c>schtasks /Create</c> refuses with "Access is denied" from an
/// ordinary shell; registering the same task through the scheduler's COM API from the same
/// shell succeeds. Both were tried, in that order, on the same unelevated session.
/// </para>
/// <para>
/// That matters more than a choice of plumbing. A broker needs no privilege the logged-in user
/// does not have — it binds a loopback port and writes under their own profile — so an install
/// that demanded an administrator would be demanding it for the tool's convenience, and the
/// first thing anybody setting Parley up would meet is a consent prompt they should not have
/// been asked for.
/// </para>
/// <para>
/// <b>Late-bound.</b> The alternative is a NuGet package wrapping the same COM interfaces, and
/// four calls do not earn a dependency — particularly one that would appear in the graph of a
/// tool that runs on three platforms and needs this on one. The cost is that mistakes here are
/// runtime mistakes, which is why the XML this hands over is composed and tested elsewhere: what
/// is left is four method calls whose failure is immediate and obvious.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ScheduledTasks
{
    /// <summary>Replace a task of the same name rather than refusing.</summary>
    /// <remarks>
    /// <c>TASK_CREATE_OR_UPDATE</c>. Somebody moving their store or changing the port installs
    /// again, and refusing would oblige them to find and delete the old entry first — with the
    /// old one still running until they did.
    /// </remarks>
    private const int CreateOrUpdate = 6;

    /// <summary>Run as the interactive user, with the token they already have.</summary>
    /// <remarks>
    /// <c>TASK_LOGON_INTERACTIVE_TOKEN</c>, which is what makes this registerable without a
    /// password and without elevation. The task runs when that user is logged on, which is
    /// exactly the lifetime the broker wants: its agents are editor sessions, and there are none
    /// when nobody is signed in.
    /// </remarks>
    private const int InteractiveToken = 3;

    /// <summary>Registers a task, replacing any of the same name.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="xml">Its definition.</param>
    /// <exception cref="InvalidOperationException">The scheduler is not available.</exception>
    public static void Register(string name, string xml)
    {
        dynamic folder = RootFolder();

        _ = folder.RegisterTask(name, xml, CreateOrUpdate, null, null, InteractiveToken, null);
    }

    /// <summary>Runs a registered task now, without waiting for its trigger.</summary>
    /// <param name="name">The task.</param>
    /// <exception cref="InvalidOperationException">The scheduler is not available.</exception>
    /// <remarks>
    /// <para>
    /// <b>Registering a task does not run it, and on the other two supervisors installing does.</b>
    /// <c>systemctl --user enable --now</c> starts the unit; a launch agent with
    /// <c>RunAtLoad</c> starts when it is bootstrapped. A scheduled task carrying a logon
    /// trigger starts at the next logon and not before, so an install that stopped at
    /// registration left Windows -- and only Windows -- with the broker still not running.
    /// </para>
    /// <para>
    /// That difference is invisible at the point it is created and arrives later as agents that
    /// will not attach, which is a long way from the command that caused it.
    /// </para>
    /// </remarks>
    public static void Start(string name)
    {
        dynamic folder = RootFolder();
        dynamic task = folder.GetTask(name);

        // The parameters an on-demand run passes to the action. There are none: everything the
        // broker needs is in the registered command line, deliberately, because a supervisor
        // starting it at logon has no way to pass anything either.
        _ = task.Run(null);
    }

    /// <summary>Removes a task, if it is there.</summary>
    /// <param name="name">The task.</param>
    /// <returns><see langword="true"/> if one was removed.</returns>
    public static bool Delete(string name)
    {
        if (!Exists(name))
        {
            return false;
        }

        dynamic folder = RootFolder();
        folder.DeleteTask(name, 0);

        return true;
    }

    /// <summary>The arguments a registered task passes to its program.</summary>
    /// <param name="name">The task.</param>
    /// <returns>The arguments of its first action, or null when there is no such task.</returns>
    /// <remarks>
    /// The installer used to read these through <c>Get-ScheduledTask</c> to carry a catalogue
    /// across a reinstall, which worked on the one platform that has that cmdlet. Read here, the
    /// same question has one answer on every platform — see <see cref="InstalledEntry"/>.
    /// </remarks>
    public static string? Arguments(string name)
    {
        if (!Exists(name))
        {
            return null;
        }

        dynamic folder = RootFolder();
        dynamic task = folder.GetTask(name);

        foreach (dynamic action in task.Definition.Actions)
        {
            // The first Exec action is the broker. The XML this tool registers has exactly one.
            return (string?)action.Arguments;
        }

        return null;
    }

    /// <summary>Whether a task is registered.</summary>
    /// <param name="name">The task.</param>
    /// <returns><see langword="true"/> if it is.</returns>
    /// <remarks>
    /// Asked by enumerating rather than by fetching, because fetching a task that is not there
    /// throws a COM exception carrying an HRESULT — which would have to be matched on a number
    /// to tell "absent" from "the scheduler is broken", and matching on HRESULTs is how the two
    /// come to be confused.
    /// </remarks>
    public static bool Exists(string name)
    {
        try
        {
            dynamic folder = RootFolder();

            foreach (dynamic task in folder.GetTasks(1))
            {
                if (string.Equals((string)task.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }

        return false;
    }

    /// <summary>Connects to the scheduler and returns its root folder.</summary>
    /// <returns>The folder.</returns>
    /// <exception cref="InvalidOperationException">The scheduler is not available.</exception>
    private static dynamic RootFolder()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException(
                "The Windows Task Scheduler is not available on this machine.");

        dynamic service = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException(
                "The Windows Task Scheduler could not be started.");

        // Connects as the current user. The overload taking a server and credentials is what
        // would reach another machine, and nothing here should.
        service.Connect();

        return service.GetFolder("\\");
    }
}
