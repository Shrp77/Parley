using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Parley.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That the agents tab opened inside a project lists that project's agents and no other's, in the answer that fills its grid.
/// </summary>
/// <remarks>
/// The grid is drawn empty and filled by a named handler, which does not run the page's own <c>OnGet</c>, where the project
/// in the address became the scope. "Agents in alpha" therefore listed every project's agents and reported other projects'
/// failures, with the scope filter hidden so it could not be narrowed (review, lens 1 run 1 on 0.64.4). It takes two
/// projects to see, which is why the one-project tests did not.
/// </remarks>
[Integration]
public sealed class TheAgentsTabInsideAProjectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Wire.RegistryRow Row(string agent) => new()
    {
        Name = agent, Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
    };

    /// <summary>A console with two projects, <c>alpha</c> holding orders and <c>beta</c> holding billing.</summary>
    private static (RoadmapConsole Console, HttpClient Client, RoadmapBroker Beta) Two()
    {
        var console = new RoadmapConsole();
        var beta = new RoadmapBroker();

        console.Broker.Registry.Add(Row("orders@test"));
        beta.Registry.Add(Row("billing@test"));

        var client = console.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<BrokerDirectory>();
            services.AddSingleton(_ => new BrokerDirectory(
                [
                    new ProjectSettings(ProjectName.Create("alpha"), "alpha.db", "http://127.0.0.1:5407", null),
                    new ProjectSettings(ProjectName.Create("beta"), "beta.db", "http://127.0.0.1:5408", null),
                ],
                address => GrpcChannel.ForAddress(
                    address,
                    new GrpcChannelOptions
                    {
                        HttpHandler = address.EndsWith("5408", StringComparison.Ordinal) ? beta : console.Broker,
                        DisposeHttpClient = false,
                    })));
        })).CreateClient();

        return (console, client, beta);
    }

    [Fact]
    public async Task Inside_a_project_the_grid_lists_that_projects_agents_only()
    {
        var (console, client, beta) = Two();

        using (console)
        using (client)
        using (beta)
        {
            var grid = await client.GetStringAsync(new Uri("/?tab=agents&project=alpha&handler=AgentTable", UriKind.Relative), Ct);

            Assert.Contains("orders@test", grid, StringComparison.Ordinal);
            Assert.DoesNotContain("billing@test", grid, StringComparison.Ordinal);
            Assert.Empty(beta.Paths);
        }
    }

    [Fact]
    public async Task Across_projects_the_grid_lists_every_projects_agents()
    {
        var (console, client, beta) = Two();

        using (console)
        using (client)
        using (beta)
        {
            var grid = await client.GetStringAsync(new Uri("/?tab=agents&handler=AgentTable", UriKind.Relative), Ct);

            Assert.Contains("orders@test", grid, StringComparison.Ordinal);
            Assert.Contains("billing@test", grid, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Inside_a_project_another_projects_broker_being_down_is_not_this_views_business()
    {
        var (console, client, beta) = Two();

        using (console)
        using (client)
        using (beta)
        {
            beta.FailsOn = "DescribeRegistry";

            var grid = await client.GetStringAsync(new Uri("/?tab=agents&project=alpha&handler=AgentTable", UriKind.Relative), Ct);

            Assert.Contains("orders@test", grid, StringComparison.Ordinal);
            Assert.DoesNotContain("beta", grid, StringComparison.Ordinal);
        }
    }
}
