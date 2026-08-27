using System.Runtime.CompilerServices;

namespace Parley.Core.Tests;

/// <summary>
/// Fixes the machine half of every agent name these tests build.
/// </summary>
/// <remarks>
/// <para>
/// An agent is <c>role@machine</c> (SPEC.md §4.1.1) and the machine is detected, so without
/// this every fixture would embed whatever the host running the suite happens to be called —
/// and the same test would need a different expected name on each developer's laptop and on
/// each CI runner.
/// </para>
/// <para>
/// A module initializer rather than a fixture because <c>MachineName.Current</c> is resolved
/// once, on first use, and a test class constructor is already too late. It also reaches the
/// servers these suites launch as child processes, which inherit the environment.
/// </para>
/// </remarks>
internal static class TestMachine
{
    /// <summary>What every test agent's machine half is.</summary>
    public const string Name = "test";

    [ModuleInitializer]
    internal static void Fix() =>
        Environment.SetEnvironmentVariable("PARLEY_MACHINE", Name);
}
