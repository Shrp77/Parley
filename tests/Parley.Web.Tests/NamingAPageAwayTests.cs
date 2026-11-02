using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Parley.Configuration;
using Parley.Core;
using Xunit;
using Parley.Client;

namespace Parley.Web.Tests;

/// <summary>
/// That the page the naming instructions send you back to actually shows the new name.
/// </summary>
/// <remarks>
/// <para>
/// The end of the bug rather than its middle. <c>ManifestCurrencyTests</c> proves the directory
/// re-reads the file; this proves the operator's own journey — render the list, run the command,
/// render the list again, in one host that was never restarted. Everything between the directory
/// and the HTML is inside it: the singleton registration, the page model that copies the list,
/// the view that renders it.
/// </para>
/// <para>
/// Worth its own fixture because the naming page ends with a promise — "come back to the project
/// list and the new name is there" — and for the life of this console that sentence was false.
/// A test that stopped at the directory would have passed on the build that shipped it.
/// </para>
/// </remarks>
[Integration]
public sealed class NamingAPageAwayTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), $"parley-naming-{Guid.NewGuid():N}");

    private readonly string _path;

    public NamingAPageAwayTests()
    {
        Directory.CreateDirectory(_folder);
        // **The root, not a manifest file.** `--config` names the directory this machine keeps its
        // Parley files in, and a project is a folder under it.
        _path = _folder;
    }

    [Fact]
    public async Task A_project_named_while_the_console_is_running_is_on_the_next_page_it_renders()
    {
        using var console = new ManifestBackedConsole(_path);
        using var client = console.CreateClient();

        var before = await client.GetStringAsync(
            new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        // The state the operator starts in, and the sentence they are told to act on.
        Assert.Contains("No projects are named on this machine", before, StringComparison.Ordinal);
        Assert.DoesNotContain("orders", before, StringComparison.Ordinal);

        // What `parley project add` does, through the same writer it uses.
        ProjectManifest.Write(
            _path,
            [new ProjectSettings(
                ProjectName.Create("orders"),
                ProjectFolder.StoreFor(_path, ProjectName.Create("orders")),
                "http://127.0.0.1:1",
                null)]);

        var after = await client.GetStringAsync(
            new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("orders", after, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "No projects are named on this machine", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_removed_while_the_console_is_running_leaves_the_page()
    {
        // The direction that matters more, because a stale row is a link to a broker this machine
        // has said it no longer tracks -- and every operator page below the list is reached
        // through that name.
        ProjectManifest.Write(
            _path,
            [new ProjectSettings(
                ProjectName.Create("billing"),
                ProjectFolder.StoreFor(_path, ProjectName.Create("billing")),
                "http://127.0.0.1:1",
                null)]);

        using var console = new ManifestBackedConsole(_path);
        using var client = console.CreateClient();

        var before = await client.GetStringAsync(
            new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("billing", before, StringComparison.Ordinal);

        ProjectManifest.Write(_path, []);

        var after = await client.GetStringAsync(
            new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("billing", after, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder the runner has not finished with. Not this test's business.
        }
    }

    /// <summary>A console reading one manifest of the test's own.</summary>
    /// <remarks>
    /// The directory is replaced rather than the environment set, because
    /// <see cref="Parley.Tests.ManifestIsolation"/> points this whole assembly's
    /// <c>PARLEY_CONFIG</c> at an absent path, and a test that moved it would move it for every
    /// class running beside this one. What is being checked is the console over a manifest-backed
    /// directory, which this builds exactly as <c>ConsoleHost</c> does.
    /// </remarks>
    private sealed class ManifestBackedConsole(string manifest) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<BrokerDirectory>();
                services.AddSingleton(_ => BrokerDirectory.FromManifest(manifest));
            });
        }
    }
}
