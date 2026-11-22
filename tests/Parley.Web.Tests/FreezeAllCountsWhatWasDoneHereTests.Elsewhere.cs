using System.Globalization;
using System.Net;
using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Parley.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>Freeze all and Unfreeze all name each agent they also changed in another project.</summary>
public sealed partial class FreezeAllCountsWhatWasDoneHereTests
{
    [Theory]
    [InlineData("freezeall", "frozen")]
    [InlineData("unfreezeall", "unfrozen")]
    public async Task An_agent_also_changed_in_another_project_is_named_with_that_project(string action, string done)
    {
        // One process serves every project an agent is in, so the act reaches them all. Only the words that build the
        // sentence were tested: with it left off the page, said of the wrong act, or naming the project acted in as well,
        // the page still counted and every test passed (review, lens 3 run A on 5de89187).
        using var console = new RoadmapConsole();
        using var beta = new RoadmapBroker();

        var frozen = action == "unfreezeall";
        console.Broker.Registry.Add(Frozen(Row("orders@test"), frozen));
        beta.Registry.Add(Frozen(Row("orders@test"), frozen));

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

        var address = new Uri($"/Projects/Act?project=alpha&action={action}&subject=alpha", UriKind.Relative);
        var described = await client.GetStringAsync(address, Ct);

        using var form = new FormUrlEncodedContent([new("__RequestVerificationToken", Token().Match(described).Groups[1].Value)]);
        using var response = await client.PostAsync(address, form, Ct);

        var after = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));

        Assert.Contains($" 'orders@test' was {done} in 'beta' too.", after, StringComparison.Ordinal);
        Assert.DoesNotContain("in 'alpha' too", after, StringComparison.Ordinal);
        Assert.DoesNotContain(frozen ? "was frozen in" : "was unfrozen in", after, StringComparison.Ordinal);
    }

    private static Wire.RegistryRow Frozen(Wire.RegistryRow row, bool frozen)
    {
        row.FrozenUtc = frozen
            ? new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture)
            : string.Empty;

        return row;
    }
}
