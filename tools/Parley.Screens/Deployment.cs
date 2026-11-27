using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Parley.Broker;
using Parley.Configuration;
using Parley.Core;
using Parley.Server;
using Parley.Web;

namespace Parley.Screens;

/// <summary>
/// A deployment of this tool's own: a root in a temporary folder, one project's broker on a loopback port, and the web
/// console reading that root, all in this process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never the machine's deployment.</b> Loading a console page takes the person's seat and marks mail delivered, so a
/// screenshot taken from a live console changes the live store. The root here is new and empty, the broker listens on a
/// port the kernel picks, and <see cref="CreateRoot"/> clears every <c>PARLEY_</c> variable this process inherited before
/// anything reads one, so nothing below can find the person's own root, broker or node catalogue.
/// </para>
/// <para>
/// In process rather than <c>parley serve</c>: a served project claims a port from the deployment's band, which on a
/// developer's machine is where the live brokers listen, and a child process would be one more thing to clean up.
/// </para>
/// </remarks>
internal sealed class Deployment : IAsyncDisposable
{
    private readonly ParleyServer _broker;
    private readonly WebApplication _console;

    private Deployment(string root, ParleyServer broker, WebApplication console, string brokerAddress, string consoleAddress)
    {
        Root = root;
        _broker = broker;
        _console = console;
        BrokerAddress = brokerAddress;
        ConsoleAddress = consoleAddress;
    }

    /// <summary>Gets the deployment root, a temporary folder of this run's own.</summary>
    public string Root { get; }

    /// <summary>Gets where the broker listens.</summary>
    public string BrokerAddress { get; }

    /// <summary>Gets where the console listens, with no trailing slash.</summary>
    public string ConsoleAddress { get; }

    /// <summary>Makes this run's deployment root and points the whole process at it.</summary>
    /// <returns>The root, a new temporary folder.</returns>
    /// <remarks>
    /// <para>
    /// Called first, before the browser or anything else starts, so no child process and no code below inherits a
    /// variable that names the person's own deployment.
    /// </para>
    /// <para>
    /// Clears every <c>PARLEY_</c> variable, then sets <see cref="ProjectManifest.PathVariable"/> to the new root:
    /// the console finds its projects there, and the node catalogue defaults to beside it. Also clears what would
    /// configure the console's web host from outside, <c>ASPNETCORE_</c> and <c>DOTNET_URLS</c>, Kestrel endpoints
    /// and allowed hosts, because a Kestrel endpoint in the environment outranks the loopback address this tool binds.
    /// </para>
    /// </remarks>
    public static string CreateRoot()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.OfType<string>().Where(Inherited).ToList())
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        var root = Directory.CreateTempSubdirectory("parley-screens-").FullName;

        Environment.SetEnvironmentVariable(ProjectManifest.PathVariable, root);

        return root;

        static bool Inherited(string name) =>
            name.StartsWith("PARLEY_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Kestrel__Endpoints", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "DOTNET_URLS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "AllowedHosts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "urls", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Starts the broker, writes the project into the root, and starts the console.</summary>
    /// <param name="root">The root <see cref="CreateRoot"/> made; deleted with the deployment, or here if it fails to start.</param>
    /// <param name="project">The project's name, already judged by the scenario's load.</param>
    /// <param name="description">Its description, or null.</param>
    /// <param name="seed">A store to copy in before the broker opens it, with the files folder beside it, or null for a new store.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>The running deployment.</returns>
    /// <remarks>
    /// A seed is copied, never opened where it lies: the broker migrates what it opens and the console's reads mark mail
    /// delivered, so only the copy in this run's root changes. Hand it a consistent copy (a SQLite backup), not a live
    /// store's main file, whose newest writes may still sit in its write-ahead log.
    /// </remarks>
    public static async Task<Deployment> StartAsync(string root, string project, string? description, string? seed, CancellationToken cancellationToken)
    {
        BrokerRuntime? runtime = null;
        ParleyServer? broker = null;
        WebApplication? console = null;

        try
        {
            var name = ProjectName.Create(project);
            var store = Path.Combine(ProjectFolder.ProjectsIn(root), project, "parley.db");

            if (seed is not null)
            {
                Seed(seed, store);
            }

            runtime = BrokerRuntime.Open(name, store, BrokerOptions.Default, TimeProvider.System);
            broker = await ParleyServer.StartAsync(runtime, "http://127.0.0.1:0", cancellationToken).ConfigureAwait(false);
            runtime = null;

            var brokerAddress = broker.Addresses[0];

            ProjectManifest.Write(root, [new ProjectSettings(name, store, brokerAddress, description)]);

            console = ConsoleHost.Build([], address: "http://127.0.0.1:0", warn: TextWriter.Null, deploymentRoot: root);

            await console.StartAsync(cancellationToken).ConfigureAwait(false);

            var consoleAddress = console.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                .Addresses.First().TrimEnd('/');

            return new Deployment(root, broker, console, brokerAddress, consoleAddress);
        }
        catch
        {
            if (console is not null)
            {
                await console.DisposeAsync().ConfigureAwait(false);
            }

            if (broker is not null)
            {
                await broker.DisposeAsync().ConfigureAwait(false);
            }

            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }

            Remove(root);
            throw;
        }
    }

    private static void Seed(string seed, string store)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.Copy(seed, store);

        var files = seed + ".files";

        if (Directory.Exists(files))
        {
            foreach (var file in Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(store + ".files", Path.GetRelativePath(files, file));
                _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _console.StopAsync().ConfigureAwait(false);
            await _console.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _broker.DisposeAsync().ConfigureAwait(false);
            Remove(Root);
        }
    }

    /// <summary>Deletes a root this run made, saying so when it cannot.</summary>
    /// <param name="root">The root.</param>
    public static void Remove(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone.
        }
        catch (Exception left) when (left is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"parley-screens: could not remove {root}: {left.Message}");
        }
    }
}
