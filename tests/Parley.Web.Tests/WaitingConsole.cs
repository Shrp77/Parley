using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Parley.Client;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// A console whose broker answers <c>Waiting</c> from a script, and nothing else.
/// </summary>
/// <remarks>
/// The only fixture here that scripts the <em>Agents</em> service rather than the operator's. That is the whole
/// point of what it tests: asking how much is waiting for a name is an agent operation a caller holding no stream
/// may make, which is what lets the console answer the question while nobody is seated.
/// </remarks>
public sealed class WaitingConsole : WebApplicationFactory<Program>
{
    /// <summary>The project this console is configured for.</summary>
    public const string Project = "alpha";

    /// <summary>The broker's script.</summary>
    public ScriptedWaiting Broker { get; } = new();

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<BrokerDirectory>();

            services.AddSingleton(_ => new BrokerDirectory(
                [new ProjectSettings(ProjectName.Create(Project), "alpha.db", "http://127.0.0.1:5406", null)],
                address => GrpcChannel.ForAddress(
                    address, new GrpcChannelOptions { HttpHandler = Broker, DisposeHttpClient = false })));
        });
    }
}

/// <summary>The Agents service's <c>Waiting</c>, answered from a script. Anything else is <c>UNIMPLEMENTED</c>.</summary>
public sealed class ScriptedWaiting : HttpMessageHandler
{
    private readonly Lock _lock = new();

    /// <summary>How many messages every answer reports.</summary>
    public int Messages { get; set; }

    /// <summary>When set, the call fails instead of answering, so "could not ask" can be told from "none".</summary>
    public bool Unreachable { get; set; }

    /// <summary>How many times the count was asked for.</summary>
    public int Asked { get; private set; }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        IMessage? reply;

        lock (_lock)
        {
            if (request.RequestUri!.AbsolutePath == "/parley.v1.Agents/Waiting")
            {
                Asked++;
                reply = Unreachable ? null : new Wire.Notice { MessagesWaiting = Messages };
            }
            else
            {
                reply = null;
            }
        }

        var bytes = reply?.ToByteArray() ?? [];
        var framed = new byte[reply is null ? 0 : 5 + bytes.Length];

        if (reply is not null)
        {
            BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(1, 4), (uint)bytes.Length);
            bytes.CopyTo(framed, 5);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = HttpVersion.Version20,
            RequestMessage = request,
            Content = new ByteArrayContent(framed),
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");

        // 12 is UNIMPLEMENTED, 14 is UNAVAILABLE.
        response.TrailingHeaders.Add(
            "grpc-status",
            (reply is null ? 14 : 0).ToString(CultureInfo.InvariantCulture));

        return response;
    }
}
