using System.Net;
using Parley.Configuration;
using Parley.Web;
using Xunit;

namespace Parley.WebUi.Tests;

/// <summary>That a listed name written in its ASCII (punycode) form is held to the same rule as any other listed name.</summary>
/// <remarks>
/// <c>HttpRequest.Host</c> decodes an <c>xn--</c> label to Unicode, while the host check and the list both hold the ASCII
/// form a browser sends, so a comparison against the decoded form never matched and a request with no
/// <c>Sec-Fetch-Site</c> under such a name was answered (review, lens 2 run S3 on 6fde4ef3).
/// </remarks>
[Integration]
public sealed class AListedIdnNameMustSayWhoAskedTests : IDisposable
{
    private const string Listed = "xn--bcher-kva.tail0000.ts.net";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-listed-idn-" + Path.GetRandomFileName());

    public AListedIdnNameMustSayWhoAskedTests()
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

    [Theory]
    [InlineData("xn--bcher-kva.tail0000.ts.net")]
    [InlineData("xn--bcher-kva.tail0000.ts.net:80")]
    [InlineData("XN--BCHER-KVA.tail0000.ts.net")]
    [InlineData("Xn--bcher-kva.TAIL0000.ts.net")]
    public async Task A_listed_punycode_name_with_no_fetch_site_is_refused(string host)
    {
        var ct = TestContext.Current.CancellationToken;

        await using var app = ConsoleHost.Build([], "http://127.0.0.1:0", new StringWriter(), deploymentRoot: _root);
        await app.StartAsync(ct);

        try
        {
            var address = new Uri(app.Urls.Single());

            Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(address, host, null, ct));
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(address, host, "none", ct));
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
