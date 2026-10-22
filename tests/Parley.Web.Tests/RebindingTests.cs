using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
/// That a request naming any host but this console's own is refused before a page sees it.
/// </summary>
/// <remarks>
/// <para>
/// The combined review gate's finding (lens 2, run 3), which predates the branch. With no host allow-list the console
/// served every Host header, so a page whose own name had been pointed at 127.0.0.1 — DNS rebinding — was same-origin
/// with it in the person's browser: it read a form, took its antiforgery token and posted it back, and the broker was
/// asked to start an agent. Antiforgery is the only thing between a browser and purge, instruct and every transcript,
/// and a token is no defence against a page that can read it.
/// </para>
/// <para>
/// The broker here records every call and answers none, so "nothing was asked of it" is an observation, not a guess.
/// </para>
/// </remarks>
public sealed class RebindingTests
{
    private const string Start = "/Projects/Act?project=alpha&action=start&subject=orders@test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_naming_a_foreign_host_is_refused_and_asks_the_broker_nothing()
    {
        using var console = new RecordingConsole();
        using var client = console.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://attacker.example:5000"),
        });

        using var page = await client.GetAsync(new Uri(Start, UriKind.Relative), Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
        Assert.DoesNotContain("__RequestVerificationToken", html, StringComparison.Ordinal);
        Assert.Empty(console.Broker.Paths);
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1:5089")]
    public async Task This_consoles_own_names_are_served(string address)
    {
        // The control: the allow-list must not refuse the console itself.
        using var console = new RecordingConsole();
        using var client = console.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri(address),
        });

        using var page = await client.GetAsync(new Uri(Start, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public async Task No_other_page_may_frame_the_console()
    {
        // Clickjacking: a page in another tab frames the console and lays its own buttons over the console's. The
        // headers are what make the person's browser refuse to draw it, so they are read off a real response.
        using var console = new RecordingConsole();
        using var client = console.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost"),
        });

        using var page = await client.GetAsync(new Uri(Start, UriKind.Relative), Ct);

        Assert.Equal("DENY", Assert.Single(page.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(page.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains(
            "frame-ancestors 'none'",
            Assert.Single(page.Headers.GetValues("Content-Security-Policy")),
            StringComparison.Ordinal);
    }

    /// <summary>A console over a broker that records every call and answers each with an error.</summary>
    private sealed class RecordingConsole : WebApplicationFactory<Program>
    {
        public RecordingBroker Broker { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<BrokerDirectory>();

                services.AddSingleton(_ => new BrokerDirectory(
                    [new ProjectSettings(ProjectName.Create("alpha"), "alpha.db", "http://127.0.0.1:5413", null)],
                    address => GrpcChannel.ForAddress(
                        address, new GrpcChannelOptions { HttpHandler = Broker, DisposeHttpClient = false })));
            });
        }
    }

    /// <summary>Records the path of every gRPC call and fails each one.</summary>
    private sealed class RecordingBroker : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly List<string> _paths = [];

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_lock)
                {
                    return [.. _paths];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            lock (_lock)
            {
                _paths.Add(request.RequestUri!.AbsolutePath);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                RequestMessage = request,
                Content = new ByteArrayContent([]),
            };

            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            response.TrailingHeaders.Add("grpc-status", 12.ToString(CultureInfo.InvariantCulture));

            return Task.FromResult(response);
        }
    }
}
