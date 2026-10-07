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
/// A console with one project whose broker answers a conversation's export and the registry from a script that a
/// test can change between reads.
/// </summary>
/// <remarks>
/// Scripted for the reason <see cref="FilesConsole"/> gives: what is checked is what the page does with an answer,
/// and this project may not reference the broker that would give a real one. Changing the script between two reads
/// is the conversation moving on, which is the whole of what following is about.
/// </remarks>
public sealed class TranscriptConsole : WebApplicationFactory<Program>
{
    /// <summary>The project this console is configured for.</summary>
    public const string Project = "alpha";

    /// <summary>The broker's script.</summary>
    public ScriptedExport Broker { get; } = new();

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<BrokerDirectory>();

            services.AddSingleton(_ => new BrokerDirectory(
                [new ProjectSettings(ProjectName.Create(Project), "alpha.db", "http://127.0.0.1:5405", null)],
                address => GrpcChannel.ForAddress(
                    address, new GrpcChannelOptions { HttpHandler = Broker, DisposeHttpClient = false })));
        });
    }
}

/// <summary>
/// The Operators service's export and registry, answered from a script at the HTTP layer the gRPC client sends
/// through. Anything else is <c>UNIMPLEMENTED</c>.
/// </summary>
public sealed class ScriptedExport : HttpMessageHandler
{
    private readonly Lock _lock = new();

    /// <summary>The messages every export returns.</summary>
    public List<Wire.Message> Messages { get; } = [];

    /// <summary>The exchanges every export returns.</summary>
    public List<Wire.ExchangeSnapshot> Exchanges { get; } = [];

    /// <summary>The wait-for graph every <c>DescribeWaiting</c> returns; empty unless a test needs one.</summary>
    public List<Wire.WaitingEdge> Edges { get; } = [];

    /// <summary>The registry rows every <c>DescribeRegistry</c> returns; empty unless a test needs the project's agents.</summary>
    public List<Wire.RegistryRow> Registry { get; } = [];

    /// <summary>How many exports were asked for.</summary>
    public int Exports { get; private set; }

    /// <summary>Adds a Request from one agent to another, with its exchange open.</summary>
    /// <param name="id">The message and exchange identifier.</param>
    /// <param name="from">The sender.</param>
    /// <param name="to">The recipient.</param>
    /// <param name="intent">What the request says, as the sender wrote it.</param>
    public void Request(long id, string from, string to, string intent)
    {
        lock (_lock)
        {
            Messages.Add(new Wire.Message
            {
                Id = id,
                ExchangeId = id,
                Type = Wire.MessageType.Request,
                From = from,
                To = to,
                ResponderModel = "test",
                PayloadJson = "{\"intent\":" + System.Text.Json.JsonSerializer.Serialize(intent) + "}",
                ConversationId = "shared",
                CreatedUtc = "2026-09-16T10:00:00Z"
            });

            Exchanges.Add(new Wire.ExchangeSnapshot
            {
                ExchangeId = id,
                State = Wire.ExchangeState.Open,
                OpenedUtc = "2026-09-16T10:00:00Z"
            });
        }
    }

    /// <summary>Adds a message carrying a payload exactly as given, for shapes a current build will not write.</summary>
    /// <param name="id">The message and exchange identifier.</param>
    /// <param name="from">The sender.</param>
    /// <param name="to">The recipient.</param>
    /// <param name="type">What kind of message it is.</param>
    /// <param name="payloadJson">The stored document, verbatim.</param>
    /// <remarks>
    /// <see cref="Request(long, string, string, string)"/> composes the payload, so it can only ever produce what
    /// this version writes — and the thing worth checking about a stored 0.5 or 0.6 message is precisely that this
    /// version did not write it.
    /// </remarks>
    public void Stored(long id, string from, string to, Wire.MessageType type, string payloadJson)
    {
        lock (_lock)
        {
            Messages.Add(new Wire.Message
            {
                Id = id,
                ExchangeId = id,
                Type = type,
                From = from,
                To = to,
                ResponderModel = "test",
                PayloadJson = payloadJson,
                ConversationId = "shared",
                CreatedUtc = "2026-09-16T10:00:00Z"
            });

            Exchanges.Add(new Wire.ExchangeSnapshot
            {
                ExchangeId = id,
                State = Wire.ExchangeState.Open,
                OpenedUtc = "2026-09-16T10:00:00Z"
            });
        }
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        IMessage? reply;

        lock (_lock)
        {
            reply = request.RequestUri!.AbsolutePath switch
            {
                "/parley.v1.Operators/Export" => Export(),
                "/parley.v1.Operators/DescribeRegistry" => Rows(),
                "/parley.v1.Operators/DescribeWaiting" => Waiting(),
                _ => null
            };
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
            Content = new ByteArrayContent(framed)
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");

        // 12 is UNIMPLEMENTED.
        response.TrailingHeaders.Add("grpc-status", (reply is null ? 12 : 0).ToString(CultureInfo.InvariantCulture));

        return response;
    }

    private Wire.RegistryReply Rows()
    {
        var reply = new Wire.RegistryReply();

        reply.Rows.AddRange(Registry);

        return reply;
    }

    private Wire.WaitingReply Waiting()
    {
        var reply = new Wire.WaitingReply();

        reply.Edges.AddRange(Edges);

        return reply;
    }

    private Wire.ExportReply Export()
    {
        Exports++;

        var reply = new Wire.ExportReply();

        reply.Messages.AddRange(Messages);
        reply.Exchanges.AddRange(Exchanges);

        return reply;
    }
}
