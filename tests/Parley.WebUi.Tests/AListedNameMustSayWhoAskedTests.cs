using System.Net;
using Parley.Configuration;
using Parley.Web;
using Xunit;

namespace Parley.WebUi.Tests;

/// <summary>That a listed name the browser does not mark is refused, because the cross-site gate cannot see who asked.</summary>
/// <remarks>
/// A browser sends <c>Sec-Fetch-Site</c> only to a secure origin. Loopback is one; a listed name forwarded over plain
/// HTTP (<c>tailscale serve --http</c>, a port proxy) is not, so an image or a navigation another site starts reached a
/// page that acts with nothing for the gate to read (review, lens 2 run A on d99806d9).
/// </remarks>
[Integration]
public sealed class AListedNameMustSayWhoAskedTests : IDisposable
{
    private const string Listed = "workstation.tail0000.ts.net";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-listed-asked-" + Path.GetRandomFileName());

    public AListedNameMustSayWhoAskedTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ParleySettings.FileName), $$"""{ "webUi": { "hosts": [ "{{Listed}}" ] } }""");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_listed_name_with_no_fetch_site_is_refused_and_a_marked_one_is_answered()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var app = ConsoleHost.Build([], "http://127.0.0.1:0", new StringWriter(), deploymentRoot: _root);
        await app.StartAsync(ct);

        try
        {
            var address = new Uri(app.Urls.Single());

            Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(address, Listed, null, ct));
            Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(address, "WorkStation.Tail0000.ts.net", null, ct));
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(address, Listed, "none", ct));
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(address, Listed, "same-origin", ct));
            Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(address, Listed, "cross-site", ct));
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(address, "127.0.0.1", null, ct));
        }
        finally
        {
            await app.StopAsync(ct);
        }
    }

    private static async Task<HttpStatusCode> StatusAsync(Uri address, string host, string? site, CancellationToken ct)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, "/brand/icon.png"));
        request.Headers.Host = host;

        if (site is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", site);
        }

        using var response = await client.SendAsync(request, ct);

        return response.StatusCode;
    }
}
