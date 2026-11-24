using System.Net;
using System.Text.RegularExpressions;
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
/// That freezing a project's agents counts those it froze in that project, when another project that also lists them cannot
/// be reached.
/// </summary>
/// <remarks>
/// The count was of agents frozen in every project that serves them, under a sentence that named one project: with another
/// project down it read "0 of 2 agents frozen in 'alpha'" while both were frozen in alpha, and the two sentences that
/// followed were alike and named neither agent (review, lens 2 on 0.64.5).
/// </remarks>
[Integration]
public sealed partial class FreezeAllCountsWhatWasDoneHereTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex Token();

    private static Wire.RegistryRow Row(string agent) => new()
    {
        Name = agent, Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
    };

    [Fact]
    public async Task Agents_frozen_here_are_counted_as_frozen_here_and_named_with_what_happened_elsewhere()
    {
        using var console = new RoadmapConsole();
        using var beta = new RoadmapBroker();

        foreach (var agent in new[] { "orders@test", "billing@test" })
        {
            console.Broker.Registry.Add(Row(agent));
            beta.Registry.Add(Row(agent));
        }

        beta.FailsOn = "DescribeRegistry";

        using var client = console.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
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

        var address = new Uri("/Projects/Act?project=alpha&action=freezeall&subject=alpha", UriKind.Relative);
        var described = await client.GetStringAsync(address, Ct);

        using var form = new FormUrlEncodedContent([new("__RequestVerificationToken", Token().Match(described).Groups[1].Value)]);
        using var response = await client.PostAsync(address, form, Ct);

        var after = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(2, console.Broker.Frozen.Count);
        Assert.Contains("2 of 2 agents frozen in 'alpha'.", after, StringComparison.Ordinal);
        Assert.Contains("2 were not frozen in every project that serves them", after, StringComparison.Ordinal);
        Assert.Contains("'orders@test': ", after, StringComparison.Ordinal);
        Assert.Contains("'billing@test': ", after, StringComparison.Ordinal);
        Assert.Contains("beta: not done.", after, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_agent_left_running_in_one_project_is_named_once_whatever_another_project_did(bool betaDown)
    {
        // A left process runs on until Kill whatever the other project answered: named when the other project stopped it, and
        // named once when the other project could not be reached and the agent is already named as not frozen everywhere
        // (review, lens 3 sweep A on 44c8e1e6).
        using var console = new RoadmapConsole();
        using var beta = new RoadmapBroker();

        console.Broker.Registry.Add(Row("orders@test"));
        beta.Registry.Add(Row("orders@test"));
        console.Broker.FreezeProcess = "left_running";
        beta.FreezeProcess = "stopping";

        if (betaDown)
        {
            beta.FailsOn = "DescribeRegistry";
        }

        using var client = console.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
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

        var address = new Uri("/Projects/Act?project=alpha&action=freezeall&subject=alpha", UriKind.Relative);
        var described = await client.GetStringAsync(address, Ct);

        using var form = new FormUrlEncodedContent([new("__RequestVerificationToken", Token().Match(described).Groups[1].Value)]);
        using var response = await client.PostAsync(address, form, Ct);

        var after = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(betaDown ? 1 : 2, console.Broker.Frozen.Count + beta.Frozen.Count);
        Assert.Equal(1, Regex.Count(after, Regex.Escape("so it runs until you end it with Kill")));
    }
}
