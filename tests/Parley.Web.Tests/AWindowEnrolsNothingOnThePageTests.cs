using System.Net;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>A broker shown by address is a window, and the console says so before it offers a form (SPEC.md §4.1.2).</summary>
[Integration]
public sealed class AWindowEnrolsNothingOnThePageTests
{
    private sealed class WindowConsole : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<BrokerDirectory>();
                services.AddSingleton(_ => new BrokerDirectory(
                    [new ProjectSettings(ProjectName.Create(ProjectManifest.WindowName), string.Empty, "http://127.0.0.1:1", null)],
                    GrpcChannel.ForAddress));
            });
    }

    [Theory]
    [InlineData("/Projects/Enrol?project=this-machine&workspace=C%3A%5Csrc%5Cinfra&role=infra", "These files will be written")]
    [InlineData("/Projects/Enrol?project=this-machine&folder=C%3A%5Csrc", "Enrol the ticked")]
    [InlineData("/Projects/Register?project=this-machine", "mcpServers")]
    public async Task The_page_says_so_and_offers_nothing_to_fill_in(string path, string form)
    {
        using var console = new WindowConsole();
        using var client = console.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Contains(EnrollmentPlan.AWindowEnrolsNothing, page, StringComparison.Ordinal);
        Assert.DoesNotContain(form, page, StringComparison.Ordinal);
    }
}
