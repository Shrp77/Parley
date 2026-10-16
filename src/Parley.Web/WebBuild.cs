using System.Reflection;

namespace Parley.Web;

/// <summary>
/// Which build of the web UI this is, as its pages say it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own rather than the broker's</b>, for the reason the terminal's <c>BuildIdentity</c> gives: the web UI and the
/// broker are separate tools that can be installed apart, and this project reaches no store, so it cannot read the
/// schema the broker's identity names. A page shows it so a person can tell which build is running.
/// </para>
/// <para>
/// Read from this assembly rather than the entry assembly. <c>parley-web-ui</c> is the usual host, but <c>ConsoleHost</c>
/// is also what a test and <c>dotnet run</c> start, and the pages are this assembly's in every case.
/// </para>
/// </remarks>
public static class WebBuild
{
    /// <summary>The package version, without the source-control suffix.</summary>
    public static string Version { get; } = Read();

    private static string Read()
    {
        var build = typeof(WebBuild).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (build is null)
        {
            return "unknown";
        }

        var plus = build.IndexOf('+', StringComparison.Ordinal);

        return plus < 0 ? build : build[..plus];
    }
}
