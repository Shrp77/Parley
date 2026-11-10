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

    /// <summary>When set before the first client, a second project whose broker never answers.</summary>
    public bool SecondProjectDown { get; set; }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<BrokerDirectory>();

            List<ProjectSettings> projects = [new ProjectSettings(ProjectName.Create(Project), "alpha.db", "http://127.0.0.1:5406", null)];

            if (SecondProjectDown)
            {
                projects.Add(new ProjectSettings(ProjectName.Create("beta"), "beta.db", $"http://127.0.0.1:{ScriptedWaiting.DownPort}", null));
            }

            services.AddSingleton(_ => new BrokerDirectory(
                projects,
                address => GrpcChannel.ForAddress(
                    address, new GrpcChannelOptions { HttpHandler = Broker, DisposeHttpClient = false })));
        });
    }
}

/// <summary>
/// The Agents service's <c>Waiting</c> and the operator's <c>DescribeWaiting</c>, answered from a script. Anything else
/// fails.
/// </summary>
public sealed class ScriptedWaiting : HttpMessageHandler
{
    /// <summary>The port of a project whose broker never answers, whatever the script says.</summary>
    public const int DownPort = 5407;

    private readonly Lock _lock = new();

    /// <summary>How many messages every answer reports.</summary>
    public int Messages { get; set; }

    /// <summary>When set, the call fails instead of answering, so "could not ask" can be told from "none".</summary>
    public bool Unreachable { get; set; }

    /// <summary>How many times the count was asked for.</summary>
    public int Asked { get; private set; }

    /// <summary>How many exchanges an agent waits on the person for, which is what the frame's count counts.</summary>
    /// <remarks>Separate from <see cref="Messages"/> because the two are different facts, and telling them apart is what
    /// the count was wrong about (owner's report, 2026-11-06: the frame said 4 and the Inbox listed 10).</remarks>
    public int Owed { get; set; }

    /// <summary>How many times what is owed was asked for.</summary>
    public int AskedOwed { get; private set; }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        IMessage? reply;

        lock (_lock)
        {
            if (request.RequestUri!.Port == DownPort)
            {
                reply = null;
            }
            else if (request.RequestUri.AbsolutePath == "/parley.v1.Agents/Waiting")
            {
                Asked++;
                reply = Unreachable ? null : new Wire.Notice { MessagesWaiting = Messages };
            }
            else if (request.RequestUri.AbsolutePath == "/parley.v1.Operators/DescribeWaiting")
            {
                AskedOwed++;
                var edges = new Wire.WaitingReply();
                edges.Edges.AddRange(Enumerable.Range(1, Owed).Select(id => new Wire.WaitingEdge
                {
                    Waiter = "orders@box",
                    WaitedOn = PersonSeat.On(ProjectName.Create(WaitingConsole.Project)).Value,
                    ExchangeId = id,
                }));
                reply = Unreachable ? null : edges;
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
