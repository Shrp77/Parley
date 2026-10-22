using System.Runtime.InteropServices;

namespace Parley.Cli;

/// <summary>
/// Lets the broker let go of the console window it was given.
/// </summary>
/// <remarks>
/// <para>
/// <b>A logon entry starting a console program puts a window on somebody's desktop, every time they
/// log in, for a process meant to be invisible.</b> The task registers with
/// <c>InteractiveToken</c> so the broker runs in the person's own session, and Windows gives a
/// console subsystem program a console. Task Scheduler's own <c>Hidden</c> setting does not help:
/// it controls whether the task appears in the Task Scheduler list, not whether a window appears.
/// </para>
/// <para>
/// <b>The alternative is rejected on a concrete ground.</b> Running the
/// task as <c>S4U</c> — "whether the user is logged on or not" — shows no window at all, because it
/// runs outside the interactive session. But the node's whole job is launching agents, and an agent
/// is a CLI the person has already authenticated: that authentication belongs to their session, so
/// a broker in session 0 would start agents that cannot log in. A window is an annoyance; agents
/// that silently cannot authenticate is a deployment that does not work.
/// </para>
/// <para>
/// So the window is let go of instead. The cost is honest and small: it exists for the moment
/// between the process starting and this running, so a logon still flashes one briefly.
/// </para>
/// <para>
/// <b>Windows only, and silently nothing elsewhere.</b> A launchd job or a systemd unit has no
/// console to detach from — there is no window on those platforms to complain about — so the flag
/// is accepted and does nothing rather than being refused, which would make one script per platform.
/// </para>
/// </remarks>
internal static class Windowless
{
    /// <summary>Detaches this process from the console window it was given, if it has one.</summary>
    /// <returns>
    /// <see langword="true"/> when a console was let go of. False on every other platform, and on
    /// Windows when there was no console to begin with — which is the ordinary case for a process
    /// whose output is already redirected.
    /// </returns>
    /// <remarks>
    /// <b>Whoever calls this must stop writing to <see cref="System.Console"/> afterwards.</b> The
    /// standard handles are invalid once the console has gone, and a write through them is at best
    /// discarded. The broker's own output goes to its log, which is an ordinary file and is
    /// unaffected; the caller redirects the console streams to nowhere so that nothing downstream
    /// has to know this happened.
    /// </remarks>
    internal static bool Detach()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return FreeConsole();
        }
        catch (EntryPointNotFoundException)
        {
            // Defensive rather than expected: `FreeConsole` has been in kernel32 since Windows 2000.
            // Reported as "no console was let go of", which is exactly what happened.
            return false;
        }
    }

    // **`DllImport` rather than `LibraryImport`**, which is the modern one and wants
    // `AllowUnsafeBlocks` turned on for the whole project. Enabling unsafe code everywhere to
    // declare one parameterless call is a poor trade: the generator's advantage is marshalling it
    // does not have to do here, since there is nothing to marshal.
    //
    // **Resolved from System32 only.** Without a search path the loader also looks in the application's own
    // directory and the working directory, so a `kernel32.dll` planted beside the tool would run in its place.
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
}
