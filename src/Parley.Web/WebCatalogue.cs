using Parley.Client;
using Parley.Configuration;
using Parley.Setup;

namespace Parley.Web;

/// <summary>Where this machine's catalogue of startable agents is, as the web console finds it.</summary>
/// <remarks>
/// One place, because enrolling writes a line into it and retiring or purging takes that line out again: two
/// resolutions that disagreed would enrol into one file and withdraw from another, and the agent would stay startable
/// by a peer's request after a person removed it.
/// </remarks>
internal static class WebCatalogue
{
    /// <summary>The catalogue's path.</summary>
    /// <param name="directory">The projects this console serves.</param>
    /// <returns>A path, which need not exist yet.</returns>
    internal static string Path(BrokerDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var manifest = directory.ManifestPath;
        var resolved = ProjectResolution.NodeConfig(null, Environment.GetEnvironmentVariable, manifest);

        return resolved.Value is { Length: > 0 } named
            ? named
            : manifest is { Length: > 0 }
                // In the root itself, as `ProjectResolution.BesideTheManifest` puts it: the manifest path is the root.
                ? System.IO.Path.Combine(System.IO.Path.GetFullPath(manifest), NodeCatalogue.DefaultFileName)
                : NodeCatalogue.DefaultFileName;
    }
}
