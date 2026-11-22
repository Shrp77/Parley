using System.Net;
using Parley.Configuration;
using Parley.Web;
using Xunit;

namespace Parley.WebUi.Tests;

/// <summary>That the running console answers a name the person listed, and still refuses every other name.</summary>
/// <remarks>
/// <b>The owner, 2026-11-22:</b> a Tailscale <c>serve</c> address forwarded to this loopback console was answered 400.
/// A real host on a real socket, because the host check is the framework's middleware, configured at build time, and a
/// test server would not run it the way Kestrel does.
/// </remarks>
[Integration]
public sealed class ListedHostNamesTests : IDisposable
{
    private const string Listed = "workstation.tail0000.ts.net";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-listed-hosts-" + Path.GetRandomFileName());

    public ListedHostNamesTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, ParleySettings.FileName),
            $$"""{ "webUi": { "hosts": [ "{{Listed}}", "*.example" ] } }""");
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
    public async Task A_listed_name_is_answered_and_every_other_name_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var warned = new StringWriter();

        await using var app = ConsoleHost.Build([], "http://127.0.0.1:0", warned, deploymentRoot: _root);
        await app.StartAsync(ct);

        try
        {
            var address = new Uri(app.Urls.Single());

            Assert.NotEqual(HttpStatusCode.BadRequest, await StatusAsync(address, Listed, ct));
            Assert.NotEqual(HttpStatusCode.BadRequest, await StatusAsync(address, "WorkStation.Tail0000.ts.net", ct));
            Assert.NotEqual(HttpStatusCode.BadRequest, await StatusAsync(address, "127.0.0.1", ct));
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(address, "anything.example", ct));
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(address, "other.tail0000.ts.net", ct));
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(address, "rebound.attacker.test", ct));
        }
        finally
        {
            await app.StopAsync(ct);
        }

        var said = warned.ToString();

        Assert.Contains("also answering to " + Listed, said, StringComparison.Ordinal);
        Assert.Contains("acts as you", said, StringComparison.Ordinal);
        Assert.Contains("\"*.example\"", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_root_only_this_machines_own_names_are_answered()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var app = ConsoleHost.Build([], "http://127.0.0.1:0", new StringWriter());
        await app.StartAsync(ct);

        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(new Uri(app.Urls.Single()), Listed, ct));
        }
        finally
        {
            await app.StopAsync(ct);
        }
    }

    private static async Task<HttpStatusCode> StatusAsync(Uri address, string host, CancellationToken ct)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, "/css/site.css"));
        request.Headers.Host = host;

        using var response = await client.SendAsync(request, ct);

        return response.StatusCode;
    }
}
