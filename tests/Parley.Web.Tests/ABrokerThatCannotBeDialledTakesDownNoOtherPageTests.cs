using System.Net;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Parley.Client;
using Parley.Core;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That one project whose broker cannot be dialled takes down no page the console draws for every project.
/// </summary>
/// <remarks>
/// <b>Found by review, round 56.</b> The file-problems banner every page draws built each project's client outside the
/// guard that turns a broker that cannot be reached into "could not say", so an endpoint the manifest accepts but gRPC
/// cannot dial answered every page with a 500.
/// </remarks>
public sealed class ABrokerThatCannotBeDialledTakesDownNoOtherPageTests
{
    [Fact]
    public async Task The_inbox_draws_beside_a_project_whose_endpoint_cannot_be_dialled()
    {
        using var console = new TwoProjects();
        using var client = console.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri("/Inbox", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // One project merely down and one whose endpoint is accepted by the manifest and refused by gRPC.
    private sealed class TwoProjects : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<BrokerDirectory>();
                services.AddSingleton(_ => new BrokerDirectory(
                    [
                        new ProjectSettings(ProjectName.Create("alpha"), "alpha.db", "http://127.0.0.1:1", null),
                        new ProjectSettings(ProjectName.Create("beta"), "beta.db", "localhost:5301", null),
                    ],
                    GrpcChannel.ForAddress));
            });
    }
}
