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
/// A console whose broker answers the registry with one agent, and records an accepted line.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only ownership fixture here whose broker answers, and that is why it exists.</b> The three tests
/// beside it in <see cref="AskingWhatItOwnsTests"/> are all run against a broker that does not listen, on
/// purpose: each asserts a guard applied before anything is sent. What no such test can reach is the write
/// itself, which needs a workspace out of a registry — and 020's quickstart walk found a defect living in
/// exactly that gap, the console asking for the unnamed launch key on a machine where every repository
/// since 009 is enrolled under <c>parley-&lt;project&gt;</c>.
/// </para>
/// <para>
/// The registry's workspace is settable, because it is what the write is aimed at and a test has to be able
/// to aim it at a repository it made.
/// </para>
/// </remarks>
public sealed class OwnershipConsole : WebApplicationFactory<Program>
{
    /// <summary>The project this console is configured for.</summary>
    public const string Project = "alpha";

    /// <summary>The broker's script.</summary>
    public ScriptedOwnership Broker { get; } = new();

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<BrokerDirectory>();

            services.AddSingleton(_ => new BrokerDirectory(
                [new ProjectSettings(ProjectName.Create(Project), "alpha.db", "http://127.0.0.1:5412", null)],
                address => GrpcChannel.ForAddress(
                    address, new GrpcChannelOptions { HttpHandler = Broker, DisposeHttpClient = false })));
        });
    }
}

/// <summary>
/// The Operators service's registry and <c>RecordOwnership</c>, answered from a script. Anything else is
/// <c>UNIMPLEMENTED</c>.
/// </summary>
public sealed class ScriptedOwnership : HttpMessageHandler
{
    private readonly Lock _lock = new();

    /// <summary>The one agent the registry holds.</summary>
    public string Agent { get; set; } = "orders@test";

    /// <summary>Where that agent says it runs.</summary>
    public string Workspace { get; set; } = string.Empty;

    /// <summary>What peers read for it now.</summary>
    public string Owns { get; set; } = string.Empty;

    /// <summary>What was recorded, in the order it arrived.</summary>
    public List<Wire.RecordOwnershipRequest> Recorded { get; } = [];

    /// <summary>Runs while a record is being answered, as the serve pass could between a surface's write and record.</summary>
    public Action? DuringRecord { get; set; }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        IMessage? reply;

        lock (_lock)
        {
            reply = request.RequestUri!.AbsolutePath switch
            {
                "/parley.v1.Operators/DescribeRegistry" => Registry(),
                "/parley.v1.Operators/RecordOwnership" => Record(body),
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
        response.TrailingHeaders.Add(
            "grpc-status", (reply is null ? 12 : 0).ToString(CultureInfo.InvariantCulture));

        return response;
    }

    private Wire.RegistryReply Registry()
    {
        var reply = new Wire.RegistryReply();

        reply.Rows.Add(new Wire.RegistryRow
        {
            Name = Agent,
            Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running,
            Tier = Wire.AgentTier.Pull,
            Adapter = "mcp",
            Workspace = Workspace,
            Owns = Owns,
            OwnsAccepted = string.Empty
        });

        return reply;
    }

    /// <summary>Records what arrived, so a test can assert on the provenance and not only the file.</summary>
    private Wire.RecordOwnershipReply Record(byte[] framed)
    {
        // The five-byte gRPC frame header comes off before the message does.
        var asked = framed.Length > 5
            ? Wire.RecordOwnershipRequest.Parser.ParseFrom(framed.AsSpan(5).ToArray())
            : new Wire.RecordOwnershipRequest();

        Recorded.Add(asked);
        DuringRecord?.Invoke();

        return new Wire.RecordOwnershipReply
        {
            Recorded = true,
            LiveStatement = Owns,
            Pending = !string.Equals(Owns, asked.Statement, StringComparison.Ordinal)
        };
    }
}
