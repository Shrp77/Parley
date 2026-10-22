using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Parley.Core;
using Parley.Client;

namespace Parley.Web.Tests;

/// <summary>
/// A console with one project whose broker is not there.
/// </summary>
/// <remarks>
/// <para>
/// The state most of this console's behaviour is about, and the one the default fixture cannot
/// reach: with no manifest, every project-scoped page redirects to the list, and a test asserting
/// "not OK" against a redirect the client follows is a test that passes by never arriving.
/// </para>
/// <para>
/// A port nothing listens on rather than a mock, because what is being checked is how the console
/// behaves when a real connection fails — and the failure a stub raises is one somebody chose.
/// </para>
/// </remarks>
public sealed class DeadBrokerConsole : WebApplicationFactory<Program>
{
    /// <summary>The project this console is configured for.</summary>
    public const string Project = "alpha";

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<BrokerDirectory>();

            services.AddSingleton(_ => new BrokerDirectory(
                [new ProjectSettings(
                    ProjectName.Create(Project), "alpha.db", "http://127.0.0.1:1", null)],
                GrpcChannel.ForAddress));
        });
    }

    /// <summary>A client that reports a redirect rather than following it.</summary>
    /// <returns>The client.</returns>
    /// <remarks>
    /// Following redirects turns "this page sent you somewhere else" into "this page answered",
    /// which is exactly the distinction several of these tests are making.
    /// </remarks>
    public HttpClient CreateStubbornClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}
