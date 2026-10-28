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
using Parley.Client.Files;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// A console with one project whose broker answers the four file operations from a script, and a launcher that
/// records instead of launching.
/// </summary>
/// <remarks>
/// <para>
/// <b>A scripted broker here, where <see cref="DeadBrokerConsole"/> argues for a port nothing listens on.</b> That
/// argument is about failure: a stub's failure is one somebody chose. What these tests check is what the page does
/// with an answer — renders it encoded, carries a filter and a page token back, describes before it deletes — and
/// this project may not reference the broker that would give a real one, because its project file is what keeps a
/// store off the console's test graph.
/// </para>
/// <para>
/// The script sits under the channel rather than beside the client, so the page's own <see cref="BrokerClient"/> and
/// the real gRPC client do the serialising: a field the client failed to send or to read would fail here.
/// </para>
/// </remarks>
public sealed class FilesConsole : WebApplicationFactory<Program>
{
    /// <summary>The project this console is configured for.</summary>
    public const string Project = "alpha";

    /// <summary>The broker's script, and what it was asked.</summary>
    public ScriptedOperators Broker { get; } = new();

    /// <summary>Every path the page asked to open, and whether it was a folder.</summary>
    public List<(string Path, bool Folder)> Launched { get; } = [];

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<BrokerDirectory>();

            services.AddSingleton(_ => new BrokerDirectory(
                [new ProjectSettings(ProjectName.Create(Project), "alpha.db", "http://127.0.0.1:5405", null)],
                address => GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = Broker, DisposeHttpClient = false })));

            services.AddSingleton(new LocalLauncher(
                (path, folder) =>
                {
                    lock (Launched)
                    {
                        Launched.Add((path, folder));
                    }

                    return true;
                },
                MachineName.Current));
        });
    }
}

/// <summary>
/// The Operators service's file operations, answered from a script at the HTTP layer the gRPC client sends through.
/// </summary>
/// <remarks>
/// Any other operation is answered <c>UNIMPLEMENTED</c>, which the console reports as a broker problem — so a page that
/// began calling something new would say so on the page rather than pass on a default.
/// </remarks>
public sealed class ScriptedOperators : HttpMessageHandler
{
    private readonly Lock _lock = new();

    /// <summary>The files every list returns.</summary>
    public List<Wire.SharedFileView> Files { get; } = [];

    /// <summary>The placement counts every list returns.</summary>
    public List<Wire.FilePlacementCount> Placements { get; } = [];

    /// <summary>The token every list returns for its next page, or empty.</summary>
    public string NextPageToken { get; set; } = string.Empty;

    /// <summary>Where a file or folder is said to be.</summary>
    public Func<Wire.FileLocationRequest, Wire.FileLocationReply> Where { get; set; } =
        _ => new Wire.FileLocationReply
        {
            Refusal = new Wire.FileRefusal { Rejection = Wire.FileRejection.NoSuchFile, Detail = "Not in the location." }
        };

    /// <summary>Every list asked for.</summary>
    public List<Wire.ListFilesRequest> Listed { get; } = [];

    /// <summary>Every removal asked for.</summary>
    public List<Wire.RemoveFilesRequest> Removed { get; } = [];

    /// <summary>How many prunes were asked for.</summary>
    public int Pruned { get; private set; }

    /// <summary>Every location asked for.</summary>
    public List<Wire.FileLocationRequest> Described { get; } = [];

    /// <summary>The conversations every conversation list returns.</summary>
    public List<string> Conversations { get; } = ["pricing"];

    /// <summary>Every placement as the person: its header and the bytes that followed it.</summary>
    public List<(Wire.PlaceFileHeader Header, byte[] Bytes)> Placed { get; } = [];

    /// <summary>What a placement as the person comes to, given its header and bytes.</summary>
    public Func<Wire.PlaceFileHeader, byte[], Wire.PlaceFileReply> Placing { get; set; } =
        (header, bytes) => new Wire.PlaceFileReply
        {
            Outcome = Wire.PlaceOutcome.Placed,
            Rejection = Wire.FileRejection.None,
            ConversationId = header.ConversationId,
            Name = header.Name,
            SizeBytes = bytes.Length,
            Sha256 = ContentHash.Of(bytes).Value
        };

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var message = body.Length >= 5 ? body[5..] : [];

        IMessage? reply;

        lock (_lock)
        {
            reply = request.RequestUri!.AbsolutePath switch
            {
                "/parley.v1.Operators/ListSharedFiles" => List(Wire.ListFilesRequest.Parser.ParseFrom(message)),
                "/parley.v1.Operators/RemoveFiles" => Remove(Wire.RemoveFilesRequest.Parser.ParseFrom(message)),
                "/parley.v1.Operators/PruneFiles" => Prune(),
                "/parley.v1.Operators/DescribeFileLocation" => Describe(Wire.FileLocationRequest.Parser.ParseFrom(message)),
                "/parley.v1.Operators/ListConversations" => ListConversations(),
                "/parley.v1.Operators/PlacePersonsFile" => Place(body),
                _ => null
            };
        }

        return Respond(request, reply);
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request, IMessage? reply)
    {
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

    private Wire.FilesReply List(Wire.ListFilesRequest asked)
    {
        Listed.Add(asked);

        var reply = new Wire.FilesReply
        {
            TotalFiles = Files.Count,
            TotalBytes = Files.Sum(f => f.SizeBytes),
            NextPageToken = NextPageToken
        };

        reply.Files.AddRange(Files);
        reply.Placements.AddRange(Placements);
        reply.Totals.AddRange(Files
            .GroupBy(f => f.ConversationId, StringComparer.Ordinal)
            .Select(g => new Wire.ConversationFilesTotal { ConversationId = g.Key, Files = g.Count(), Bytes = g.Sum(f => f.SizeBytes) }));

        return reply;
    }

    private Wire.RemoveFilesReply Remove(Wire.RemoveFilesRequest asked)
    {
        Removed.Add(asked);

        // NONE said outright, as the broker says it: zero on this enum is UNSPECIFIED, which a client is right to
        // refuse as a reason it does not know.
        var reply = new Wire.RemoveFilesReply
        {
            Rejection = Wire.FileRejection.None,
            BytesFreed = Files.Where(f => asked.Files.Any(r => r.Name == f.Name && r.ConversationId == f.ConversationId)).Sum(f => f.SizeBytes)
        };

        reply.Removed.AddRange(asked.Files);

        return reply;
    }

    private Wire.PruneFilesReply Prune()
    {
        Pruned++;

        return new Wire.PruneFilesReply { FilesRemoved = Files.Count, BytesFreed = Files.Sum(f => f.SizeBytes) };
    }

    private Wire.ListConversationsReply ListConversations()
    {
        var reply = new Wire.ListConversationsReply();

        reply.Conversations.AddRange(Conversations.Select(id => new Wire.ConversationSummary { ConversationId = id }));

        return reply;
    }

    /// <summary>Reads a client stream's frames, each a gRPC message behind its five-byte prefix.</summary>
    private Wire.PlaceFileReply Place(byte[] body)
    {
        Wire.PlaceFileHeader? header = null;
        using var bytes = new MemoryStream();

        for (var at = 0; at + 5 <= body.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(at + 1, 4));
            var frame = Wire.PlaceFileFrame.Parser.ParseFrom(body.AsSpan(at + 5, length));

            if (frame.FrameCase == Wire.PlaceFileFrame.FrameOneofCase.Header)
            {
                header = frame.Header;
            }
            else
            {
                frame.Chunk.WriteTo(bytes);
            }

            at += 5 + length;
        }

        var content = bytes.ToArray();

        Placed.Add((header!, content));

        return Placing(header!, content);
    }

    private Wire.FileLocationReply Describe(Wire.FileLocationRequest asked)
    {
        Described.Add(asked);

        return Where(asked);
    }
}
