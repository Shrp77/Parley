using System.Reflection;

namespace Parley.Top;

/// <summary>
/// Which build of the terminal this is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own rather than the broker's, and deliberately so.</b> `parley` has
/// <c>BuildIdentity</c>, which names the package, the commit, the protocol, the schema it
/// migrates a store to and the runtime. Two of those are not this tool's to say: it opens no
/// store, so a schema number here would be a claim about somebody else's build.
/// </para>
/// <para>
/// It also could not borrow that type if it wanted to. The broker's version reads the schema
/// number, so it lives in an assembly that references the one owning the store — and there is no
/// route to a store on this project's graph, nor may there be.
/// </para>
/// <para>
/// The number matters more here than it did when there was one binary. This tool and the broker
/// are installed separately now, so they can genuinely differ — and the first question about a
/// terminal showing something unexpected is which one is running.
/// </para>
/// </remarks>
internal static class BuildIdentity
{
    /// <summary>The package version, without the source-control suffix.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Reads the informational version and drops any suffix.</summary>
    /// <returns>The version, or <c>unknown</c> when the assembly carries none.</returns>
    private static string ReadVersion()
    {
        var build = typeof(BuildIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (build is null)
        {
            return "unknown";
        }

        var plus = build.IndexOf('+', StringComparison.Ordinal);

        return plus < 0 ? build : build[..plus];
    }
}
