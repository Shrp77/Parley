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
/// A console with one project whose broker answers the file operations, and the file problems, from a script, and a
/// launcher that records instead of launching.
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

    /// <summary>Where the page's copies go: a folder of this console's own, never the person's application data.</summary>
    public string Copies { get; } = Path.Combine(Path.GetTempPath(), "parley-files-console-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        try
        {
            if (Directory.Exists(Copies))
            {
                Directory.Delete(Copies, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a copy a test left open.
        }
    }

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
                MachineName.Current,
                Copies));
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

    /// <summary>The bytes a read of each file returns, by name; a name not here is refused as no such file.</summary>
    public Dictionary<string, byte[]> Contents { get; } = new(StringComparer.Ordinal);

    /// <summary>Every read of a file asked for.</summary>
    public List<Wire.ReadSharedFileRequest> Read { get; } = [];

    /// <summary>Every list asked for.</summary>
    public List<Wire.ListFilesRequest> Listed { get; } = [];

    /// <summary>Every removal asked for.</summary>
    public List<Wire.RemoveFilesRequest> Removed { get; } = [];

    /// <summary>How many prunes were asked for.</summary>
    public int Pruned { get; private set; }

    /// <summary>Every location asked for.</summary>
    public List<Wire.FileLocationRequest> Described { get; } = [];

    /// <summary>The problems every list of file problems returns, waiting or dismissed.</summary>
    public List<Wire.FileProblemView> Problems { get; } = [];

    /// <summary>Every list of file problems asked for.</summary>
    public List<Wire.ListFileProblemsRequest> ProblemsListed { get; } = [];

    /// <summary>Every problem dismissed, by its identifier.</summary>
    public List<long> Dismissed { get; } = [];

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

        if (request.RequestUri!.AbsolutePath == "/parley.v1.Operators/PlacePersonsFile")
        {
            return PlaceStreaming(request, cancellationToken);
        }

        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var message = body.Length >= 5 ? body[5..] : [];

        if (request.RequestUri!.AbsolutePath == "/parley.v1.Operators/ReadSharedFile")
        {
            lock (_lock)
            {
                return RespondMany(request, ReadFile(Wire.ReadSharedFileRequest.Parser.ParseFrom(message)));
            }
        }

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
                "/parley.v1.Operators/ListFileProblems" => ListProblems(Wire.ListFileProblemsRequest.Parser.ParseFrom(message)),
                "/parley.v1.Operators/DismissFileProblem" => Dismiss(Wire.DismissFileProblemRequest.Parser.ParseFrom(message)),
                _ => null
            };
        }

        return Respond(request, reply);
    }

    // A placement waits for the go-ahead before it sends its bytes, so the response starts before the request has been
    // read: the go-ahead at once, and the reply once the client has ended its stream.
    private HttpResponseMessage PlaceStreaming(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pipe = new System.IO.Pipelines.Pipe();

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = HttpVersion.Version20,
            RequestMessage = request,
            Content = new StreamContent(pipe.Reader.AsStream())
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");

        _ = Task.Run(
            async () =>
            {
                var answers = pipe.Writer.AsStream();

                try
                {
                    await WriteFrameAsync(answers, new Wire.PlaceFileAnswer { GoAhead = new Wire.Empty() }, cancellationToken);

                    var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    Wire.PlaceFileReply reply;

                    lock (_lock)
                    {
                        reply = Place(body);
                    }

                    await WriteFrameAsync(answers, new Wire.PlaceFileAnswer { Reply = reply }, cancellationToken);
                    response.TrailingHeaders.Add("grpc-status", "0");
                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception failed)
                {
                    await pipe.Writer.CompleteAsync(failed);
                }
            },
            cancellationToken);

        return response;
    }

    private static async Task WriteFrameAsync(Stream to, IMessage message, CancellationToken cancellationToken)
    {
        var bytes = message.ToByteArray();
        var framed = new byte[5 + bytes.Length];

        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(1, 4), (uint)bytes.Length);
        bytes.CopyTo(framed, 5);

        await to.WriteAsync(framed, cancellationToken);
        await to.FlushAsync(cancellationToken);
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

    // As the broker streams it: a header and the bytes, or a refusal in place of the header.
    private List<IMessage> ReadFile(Wire.ReadSharedFileRequest asked)
    {
        Read.Add(asked);

        var file = Files.FirstOrDefault(f => f.Name == asked.Name && f.ConversationId == asked.ConversationId);

        if (file is null || !Contents.TryGetValue(asked.Name, out var bytes))
        {
            return [new Wire.FetchFileFrame { Refusal = new Wire.FileRefusal { Rejection = Wire.FileRejection.NoSuchFile } }];
        }

        if (asked.ExpectSha256.Length > 0 && asked.ExpectSha256 != file.Sha256)
        {
            return [new Wire.FetchFileFrame { Refusal = new Wire.FileRefusal { Rejection = Wire.FileRejection.Changed } }];
        }

        return
        [
            new Wire.FetchFileFrame { Header = file },
            new Wire.FetchFileFrame { Chunk = ByteString.CopyFrom(bytes) },
        ];
    }

    private static HttpResponseMessage RespondMany(HttpRequestMessage request, IReadOnlyList<IMessage> replies)
    {
        using var framed = new MemoryStream();

        foreach (var reply in replies)
        {
            var bytes = reply.ToByteArray();
            var prefix = new byte[5];

            BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(1, 4), (uint)bytes.Length);
            framed.Write(prefix);
            framed.Write(bytes);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = HttpVersion.Version20,
            RequestMessage = request,
            Content = new ByteArrayContent(framed.ToArray())
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        response.TrailingHeaders.Add("grpc-status", "0");

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

    /// <summary>The parent every listed conversation names.</summary>
    public string Parent { get; set; } = string.Empty;

    private Wire.ListConversationsReply ListConversations()
    {
        var reply = new Wire.ListConversationsReply();

        reply.Conversations.AddRange(Conversations.Select(id => new Wire.ConversationSummary { ConversationId = id, Parent = Parent }));

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

    private Wire.FileProblemsReply ListProblems(Wire.ListFileProblemsRequest asked)
    {
        ProblemsListed.Add(asked);

        var reply = new Wire.FileProblemsReply { Machine = MachineName.Current, QuarantinePath = "/srv/parley/alpha.db.files-quarantine" };

        reply.Problems.AddRange(Problems.Where(p => asked.IncludeDismissed || p.DismissedUtc.Length == 0));

        return reply;
    }

    private Wire.DidItReply Dismiss(Wire.DismissFileProblemRequest asked)
    {
        Dismissed.Add(asked.Id);

        var problem = Problems.FirstOrDefault(p => p.Id == asked.Id && p.DismissedUtc.Length == 0);

        if (problem is not null)
        {
            problem.DismissedUtc = "2026-09-14T10:00:00Z";
        }

        return new Wire.DidItReply { Changed = problem is not null };
    }

    private Wire.FileLocationReply Describe(Wire.FileLocationRequest asked)
    {
        Described.Add(asked);

        return Where(asked);
    }
}
