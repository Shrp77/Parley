using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Parley.Mcp;

/// <summary>Which process hosts this adapter: the one that started it (SPEC.md §11.1).</summary>
/// <remarks>
/// <para>
/// <b>Said to the broker as a label, never as authority.</b> A person may end a process a node that is gone left running,
/// and the adapter is the only thing that can name it: the node that started it is dead and the broker forgot what it
/// was told. A node ends only a process of the agent's own kind, whatever is named.
/// </para>
/// <para>
/// <b>Asked of the operating system, not of anything the client passes</b>, because a client passes nothing about it. Any
/// failure is no answer: an adapter that cannot say who hosts it attaches as it always did, and the process cannot then
/// be named, which is what a person is told.
/// </para>
/// </remarks>
public static class HostProcess
{
    /// <summary>The process that started this one.</summary>
    /// <returns>Its identifier, or <see langword="null"/> when the system would not say.</returns>
    public static long? Id()
    {
        try
        {
            var id = OperatingSystem.IsWindows() ? FromWindows()
                : OperatingSystem.IsLinux() ? FromProc()
                : FromPs();

            return id is > 0 ? id : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or FormatException or EntryPointNotFoundException or DllNotFoundException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static long? FromWindows()
    {
        using var self = Process.GetCurrentProcess();
        var info = default(ProcessBasicInformation);

        return NtQueryInformationProcess(self.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
            ? (long)info.InheritedFromUniqueProcessId
            : null;
    }

    /// <summary>Reads <c>/proc/self/stat</c>, whose second field is the name and may hold spaces and brackets.</summary>
    private static long? FromProc()
    {
        var stat = File.ReadAllText("/proc/self/stat");
        var close = stat.LastIndexOf(')');

        // After the name: the state, then the parent.
        var rest = close < 0 ? [] : stat[(close + 1)..].Trim().Split(' ');

        return rest.Length > 1 ? long.Parse(rest[1], CultureInfo.InvariantCulture) : null;
    }

    private static long? FromPs()
    {
        using var ps = Process.Start(new ProcessStartInfo("ps")
        {
            ArgumentList = { "-o", "ppid=", "-p", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });

        if (ps is null)
        {
            return null;
        }

        var said = ps.StandardOutput.ReadToEnd();

        return ps.WaitForExit(2000) ? long.Parse(said.Trim(), CultureInfo.InvariantCulture) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public UIntPtr UniqueProcessId;
        public UIntPtr InheritedFromUniqueProcessId;
    }

    // `DllImport` for the reason Windowless gives, resolved from System32 only so a planted DLL is never loaded.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int informationClass, ref ProcessBasicInformation information, int size, out int returned);
}
