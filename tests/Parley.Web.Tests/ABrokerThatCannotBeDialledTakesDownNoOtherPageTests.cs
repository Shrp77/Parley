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
/// That a project whose broker cannot be dialled takes down no page: neither one drawn for every project nor its own.
/// </summary>
/// <remarks>
/// <b>Found by review, round 56.</b> The file-problems banner every page draws built each project's client outside the
/// guard that turns a broker that cannot be reached into "could not say", so an endpoint the manifest accepts but gRPC
/// cannot dial answered every page with a 500.
/// </remarks>
public sealed class ABrokerThatCannotBeDialledTakesDownNoOtherPageTests
{
    [Theory]
    [InlineData("/Inbox")]
    [InlineData("/Notifications")]
    [InlineData("/Projects/Agents?project=beta")]
    [InlineData("/Projects/FileProblems?project=beta")]
    [InlineData("/Projects/Files?project=beta")]
    [InlineData("/Projects/Health?project=beta")]
    [InlineData("/Projects/Ownership?project=beta")]
    [InlineData("/Conversations/Destroy?project=beta&id=pricing")]
    [InlineData("/Roadmaps?project=beta")]
    [InlineData("/Roadmaps/Roadmap?project=beta&id=1")]
    [InlineData("/Roadmaps/Edit?project=beta&id=1")]
    [InlineData("/Roadmaps/Proposal?project=beta&id=1")]
    public async Task A_page_beside_or_for_a_project_whose_endpoint_cannot_be_dialled_draws(string path)
    {
        // Round 62: the client was made outside every guard, and making it opened the channel, so these answered 500.
        using var console = new TwoProjects();
        using var client = console.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

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
