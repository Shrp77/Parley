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
/// A console whose broker answers the roadmap operations from a script, and records what it was asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Records the requests rather than the replies, because the claims worth checking are about what the page sent.</b>
/// That a GET sent no confirm, that a POST sent the content the person wrote and not the proposal's, and that declining
/// went through its own page: each is read off <see cref="RoadmapBroker.Confirmed"/> and
/// <see cref="RoadmapBroker.Declined"/>.
/// </para>
/// </remarks>
public sealed class RoadmapConsole : WebApplicationFactory<Program>
{
    /// <summary>The project this console is configured for.</summary>
    public const string Project = "alpha";

    /// <summary>The broker's script.</summary>
    public RoadmapBroker Broker { get; } = new();

    /// <summary>A deployment root to follow, so pages that read or write it (settings.json) have one; none by default.</summary>
    public string? Root { get; init; }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<BrokerDirectory>();

            GrpcChannel Connect(string address) =>
                GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = Broker, DisposeHttpClient = false });

            if (Root is { } root)
            {
                Parley.Configuration.ProjectManifest.Write(
                    root, [new ProjectSettings(ProjectName.Create(Project), "alpha.db", "http://127.0.0.1:5407", null)]);
                services.AddSingleton(_ => BrokerDirectory.FollowingRoot(root, Connect));
            }
            else
            {
                services.AddSingleton(_ => new BrokerDirectory(
                    [new ProjectSettings(ProjectName.Create(Project), "alpha.db", "http://127.0.0.1:5407", null)],
                    Connect));
            }
        });
    }
}

/// <summary>The roadmap operations, answered from a script. Anything else is <c>UNIMPLEMENTED</c>.</summary>
public sealed class RoadmapBroker : HttpMessageHandler
{
    private readonly Lock _lock = new();

    /// <summary>The proposals every read returns.</summary>
    public List<Wire.ProposalDetail> Proposals { get; } = [];

    /// <summary>The roadmaps every list returns.</summary>
    public List<Wire.RoadmapSummary> Roadmaps { get; } = [];

    /// <summary>The roadmaps <c>Roadmap</c> returns, by identifier.</summary>
    public Dictionary<long, Wire.RoadmapDetail> Details { get; } = [];

    /// <summary>The conversations every list returns.</summary>
    public List<Wire.ConversationSummary> Conversations { get; } = [];

    /// <summary>How many conversations one listing returns before it hands back a token for the rest; 0 returns them all at once.</summary>
    /// <remarks>The broker lists fifty at a time, so a page that reads only the first answer reads only the newest fifty.</remarks>
    public int ConversationPageSize { get; set; }

    /// <summary>Whether a listing leaves archived conversations out unless it asks for them, as the broker does.</summary>
    /// <remarks>Off unless a test sets it, so a fixture that marks a conversation archived only to draw it is still listed.</remarks>
    public bool OmitsArchived { get; set; }

    /// <summary>The agents the registry lists, none by default.</summary>
    public List<Wire.RegistryRow> Registry { get; } = [];

    /// <summary>Every confirmation the broker was asked for.</summary>
    public List<Wire.ConfirmProposalRequest> Confirmed { get; } = [];

    /// <summary>Every proposal it was asked to decline.</summary>
    public List<long> Declined { get; } = [];

    /// <summary>Every edit it was asked for.</summary>
    public List<Wire.EditRoadmapRequest> Edited { get; } = [];

    /// <summary>Who waits on whom, as <c>DescribeWaiting</c> answers; none by default.</summary>
    public List<Wire.WaitingEdge> Edges { get; } = [];

    /// <summary>Messages an export of their conversation carries; none by default.</summary>
    public List<Wire.Message> ExportMessages { get; } = [];

    /// <summary>Exchanges every export carries, as when retention has removed a conversation's messages and kept them.</summary>
    public List<Wire.ExchangeSnapshot> ExportExchanges { get; } = [];

    /// <summary>What <c>DescribeFlags</c> answers; none by default.</summary>
    public List<Wire.FlagView> Flags { get; } = [];

    /// <summary>Every Result the person sent, in order. Each one taken closes its exchange, as the broker does.</summary>
    public List<Wire.ResultRequest> Resulted { get; } = [];

    /// <summary>Every Clarify sent as the person.</summary>
    public List<Wire.ClarifyRequest> Clarified { get; } = [];

    /// <summary>Every stop a person asked for.</summary>
    public List<Wire.StopRequest> Stopped { get; } = [];

    /// <summary>The starts a person asked for.</summary>
    public List<Wire.StartRequest> Started { get; } = [];

    /// <summary>What a start is answered with: false for a node that was not asked.</summary>
    public bool StartAccepted { get; set; } = true;

    /// <summary>
    /// When set, a start is refused as the broker refuses one it will not ask for (a frozen agent, or one open outside
    /// Parley): the call ends <c>FAILED_PRECONDITION</c> with this sentence as its detail.
    /// </summary>
    public string? StartRefusal { get; set; }

    /// <summary>Every conversation a person asked to be ended, with the reason given.</summary>
    public List<Wire.ForceConcludeRequest> Concluded { get; } = [];

    /// <summary>The conversations an ending changes nothing in, as one already closed.</summary>
    public HashSet<string> AlreadyClosed { get; } = new(StringComparer.Ordinal);

    /// <summary>Every restart a person asked for.</summary>
    public List<Wire.StopRequest> Restarted { get; } = [];

    /// <summary>Every kill of a process a node that is gone left running it was asked for.</summary>
    public List<Wire.StopRequest> Killed { get; } = [];

    /// <summary>Every freeze of an agent a person asked for, with whether it was to outlast a start of the broker.</summary>
    public List<Wire.FreezeRequest> Frozen { get; } = [];

    /// <summary>Every unfreeze of an agent a person asked for.</summary>
    public List<Wire.AgentRequest> Unfrozen { get; } = [];

    /// <summary>What a freeze says became of the agent's process, in the wire's words; <c>not_running</c> by default.</summary>
    public string FreezeProcess { get; set; } = "not_running";

    /// <summary>When set, the proposals read is not implemented, as a broker older than roadmaps would answer.</summary>
    public bool Old { get; set; }

    /// <summary>What a confirmation or edit answers; refused when set.</summary>
    public string? Refuse { get; set; }

    /// <summary>How many calls were made at all, so "nothing was sent" is a count and not an absence.</summary>
    public int Calls { get; private set; }

    /// <summary>
    /// When set, the person's seat is held (an Attach answered and left open) and a Request is accepted, so a send from
    /// the page reaches its successful end. The registry must also list the person's seat.
    /// </summary>
    public bool Seats { get; set; }

    /// <summary>What the broker says it did about the receiver of a Request.</summary>
    public Wire.WakeOutcome RequestWake { get; set; }

    /// <summary>Every placement as the person, with its header and bytes.</summary>
    public List<(Wire.PlaceFileHeader Header, byte[] Bytes)> Placed { get; } = [];

    /// <summary>What a placement comes to, given its header and bytes.</summary>
    public Func<Wire.PlaceFileHeader, byte[], Wire.PlaceFileReply> Placing { get; set; } =
        (header, bytes) => new Wire.PlaceFileReply
        {
            Outcome = Wire.PlaceOutcome.Placed,
            Rejection = Wire.FileRejection.None,
            ConversationId = header.ConversationId,
            Name = header.Name,
            SizeBytes = bytes.Length,
            Sha256 = Parley.Core.ContentHash.Of(bytes).Value,
        };

    private HttpResponseMessage PlaceStreaming(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pipe = new System.IO.Pipelines.Pipe();

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Version = System.Net.HttpVersion.Version20,
            RequestMessage = request,
            Content = new StreamContent(pipe.Reader.AsStream()),
        };

        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");

        _ = Task.Run(
            async () =>
            {
                var answers = pipe.Writer.AsStream();

                try
                {
                    await WriteAnswerAsync(answers, new Wire.PlaceFileAnswer { GoAhead = new Wire.Empty() }, cancellationToken);

                    var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    Wire.PlaceFileReply reply;

                    lock (_lock)
                    {
                        reply = Place(body);
                    }

                    await WriteAnswerAsync(answers, new Wire.PlaceFileAnswer { Reply = reply }, cancellationToken);
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

    private static async Task WriteAnswerAsync(Stream answers, Wire.PlaceFileAnswer answer, CancellationToken cancellationToken)
    {
        var message = answer.ToByteArray();
        var prefix = new byte[5];

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(1), (uint)message.Length);
        await answers.WriteAsync(prefix, cancellationToken);
        await answers.WriteAsync(message, cancellationToken);
        await answers.FlushAsync(cancellationToken);
    }

    private Wire.PlaceFileReply Place(byte[] body)
    {
        Wire.PlaceFileHeader? header = null;
        using var bytes = new MemoryStream();

        for (var at = 0; at + 5 <= body.Length;)
        {
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(at + 1, 4));
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

    /// <summary>When set with <see cref="Seats"/>, the seat's stream says, after the attach, that the store is being rewritten.</summary>
    public bool Tidying { get; set; }

    /// <summary>What a spin-off answers, when it should refuse.</summary>
    public string SpinOffRefusal { get; set; } = string.Empty;

    /// <summary>The end of the one operation's path that cannot reach the broker, when one should fail.</summary>
    public string FailsOn { get; set; } = string.Empty;

    /// <summary>Every operation asked of this broker, by its path.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Paths { get; } = new();

    /// <summary>How many messages the person's seat is told are waiting. This script serves no delivery stream.</summary>
    public int MessagesWaiting { get; set; }

    /// <summary>Every spin-off asked for.</summary>
    public List<Wire.SpinOffRequest> SpunOff { get; } = [];

    /// <summary>Every spin-off taken back.</summary>
    public List<Wire.SpinOffRequest> Abandoned { get; } = [];

    /// <summary>Who the seat's Requests are refused for.</summary>
    public HashSet<string> RefusesRequestsTo { get; } = new(StringComparer.Ordinal);

    /// <summary>Every Request the seat sent.</summary>
    public List<Wire.OpenRequest> Requested { get; } = [];

    /// <summary>Adds a waiting proposal.</summary>
    /// <param name="id">Its identifier.</param>
    /// <param name="proposer">Who proposed it.</param>
    /// <param name="title">Its title.</param>
    /// <param name="items">The items' words.</param>
    /// <param name="target">The title hint, or <see langword="null"/>.</param>
    public void Propose(long id, string proposer, string title, string[] items, string? target = null)
    {
        var detail = new Wire.ProposalDetail
        {
            Id = id,
            Proposer = proposer,
            TargetTitle = target ?? string.Empty,
            Content = new Wire.RoadmapContent { Title = title },
            State = Wire.ProposalState.Waiting,
            CreatedUtc = "2026-10-06T09:00:00Z",
        };

        foreach (var text in items)
        {
            detail.Content.Items.Add(new Wire.RoadmapItemDraft { Text = text });
        }

        lock (_lock)
        {
            Proposals.Add(detail);
        }
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Paths.Enqueue(request.RequestUri!.AbsolutePath);

        if (FailsOn.Length > 0 && request.RequestUri!.AbsolutePath.EndsWith(FailsOn, StringComparison.Ordinal))
        {
            throw new HttpRequestException("The scripted broker is down for this operation.");
        }

        ArgumentNullException.ThrowIfNull(request);

        if (Seats && request.RequestUri!.AbsolutePath == "/parley.v1.Operators/PlacePersonsFile")
        {
            return PlaceStreaming(request, cancellationToken);
        }

        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var payload = body.Length > 5 ? body.AsSpan(5).ToArray() : [];

        // The seat: the broker's first event, and then a stream that stays open as long as the seat is held.
        if (Seats && request.RequestUri!.AbsolutePath == "/parley.v1.Agents/Attach")
        {
            lock (_lock)
            {
                Calls++;
            }

            var attached = Frame(new Wire.Event { Attached = new Wire.Attached() });

            if (Tidying)
            {
                attached = [.. attached, .. Frame(new Wire.Event { Tidying = new Wire.Tidying { Underway = true } })];
            }
            var held = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                RequestMessage = request,
                Content = new StreamContent(new HeldOpen(attached, cancellationToken)),
            };

            held.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");

            return held;
        }

        IMessage? reply;

        lock (_lock)
        {
            Calls++;

            if (Seats && request.RequestUri!.AbsolutePath == "/parley.v1.Agents/Request")
            {
                var open = Wire.OpenRequest.Parser.ParseFrom(payload);
                Requested.Add(open);
            }

            if (Seats && request.RequestUri!.AbsolutePath == "/parley.v1.Agents/Clarify")
            {
                Clarified.Add(Wire.ClarifyRequest.Parser.ParseFrom(payload));
            }

            if (Seats && request.RequestUri!.AbsolutePath == "/parley.v1.Agents/Result")
            {
                var result = Wire.ResultRequest.Parser.ParseFrom(payload);
                Resulted.Add(result);

                // Closed as the broker closes it: nobody waits on anybody over it any more.
                _ = Edges.RemoveAll(edge => edge.ExchangeId == result.ExchangeId);

                foreach (var exchange in ExportExchanges.Where(exchange => exchange.ExchangeId == result.ExchangeId))
                {
                    exchange.State = Wire.ExchangeState.Closed;
                }
            }

            reply = request.RequestUri!.AbsolutePath switch
            {
                "/parley.v1.Operators/Proposals" => Old ? null : ListProposals(Wire.ProposalsRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/Proposal" => Old ? null : OneProposal(Wire.ProposalRequest.Parser.ParseFrom(payload).Id),
                "/parley.v1.Operators/ProposalCount" => Old
                    ? null
                    : new Wire.ProposalCountReply { Count = Proposals.Count(p => p.State == Wire.ProposalState.Waiting) },
                "/parley.v1.Operators/Roadmaps" => ListRoadmaps(),
                "/parley.v1.Operators/Roadmap" => One(Wire.RoadmapRequest.Parser.ParseFrom(payload).Id),
                "/parley.v1.Operators/ConfirmProposal" => Confirm(Wire.ConfirmProposalRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/DeclineProposal" => Decline(Wire.ProposalRequest.Parser.ParseFrom(payload).Id),
                "/parley.v1.Operators/EditRoadmap" => Edit(Wire.EditRoadmapRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/DescribeRegistry" => new Wire.RegistryReply { Rows = { Registry } },
                "/parley.v1.Operators/DescribeWaiting" => new Wire.WaitingReply { Edges = { Edges } },
                "/parley.v1.Operators/ListConversations" => Listed(Wire.ListConversationsRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/Export" => Export(Wire.ExportRequest.Parser.ParseFrom(payload).ConversationId),
                "/parley.v1.Operators/Implements" => new Wire.ImplementsReply { ProtocolVersion = Parley.Core.ProtocolVersion.Current },
                "/parley.v1.Operators/SpinOff" => Spun(Wire.SpinOffRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/AbandonSpinOff" => Taken(Wire.SpinOffRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/Start" => AskedToStart(Wire.StartRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/ForceConclude" => AskedToEnd(Wire.ForceConcludeRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/Stop" => Asked(Stopped, Wire.StopRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/Restart" => Asked(Restarted, Wire.StopRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/Kill" => Asked(Killed, Wire.StopRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/Freeze" => AskedToFreeze(Wire.FreezeRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/Unfreeze" => AskedToUnfreeze(Wire.AgentRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Agents/Request" when Seats && RefusesRequestsTo.Contains(Requested[^1].To) => new Wire.RequestReply
                {
                    Outcomes = { new Wire.RequestOutcome { Ok = false, Rejection = Wire.RequestRejection.Duplicate } },
                },
                "/parley.v1.Agents/Request" when Seats => new Wire.RequestReply
                {
                    Outcomes = { new Wire.RequestOutcome { Ok = true, ExchangeId = 7, ConversationId = "rm1-1", Wake = RequestWake } },
                },
                "/parley.v1.Agents/Waiting" when Seats => new Wire.Notice { MessagesWaiting = MessagesWaiting },
                "/parley.v1.Agents/Result" when Seats => new Wire.SayReply { Ok = true, MessageId = 900 },
                "/parley.v1.Agents/Clarify" when Seats => new Wire.SayReply { Ok = true, MessageId = 901 },
                "/parley.v1.Operators/DescribeFlags" => new Wire.FlagsReply { Flags = { Flags } },
                "/parley.v1.Operators/LocateExchange" => new Wire.LocateExchangeReply
                {
                    ConversationId = ExportMessages
                        .FirstOrDefault(m => m.ExchangeId == Wire.LocateExchangeRequest.Parser.ParseFrom(payload).ExchangeId)
                        ?.ConversationId ?? string.Empty,
                },
                _ => null,
            };
        }

        var framed = reply is null ? [] : Frame(reply);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = HttpVersion.Version20,
            RequestMessage = request,
            Content = new ByteArrayContent(framed),
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");

        if (StartRefusal is { } refusal && request.RequestUri!.AbsolutePath == "/parley.v1.Operators/Start")
        {
            // A refusal carries no message: only the status and its sentence, which the wire sends percent-encoded. 9 is
            // FAILED_PRECONDITION.
            response.Content = new ByteArrayContent([]);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            response.TrailingHeaders.Add("grpc-status", "9");
            response.TrailingHeaders.Add("grpc-message", Uri.EscapeDataString(refusal));

            return response;
        }

        // 12 is UNIMPLEMENTED.
        response.TrailingHeaders.Add("grpc-status", (reply is null ? 12 : 0).ToString(CultureInfo.InvariantCulture));

        return response;
    }

    private Wire.SpinOffReply Spun(Wire.SpinOffRequest asked)
    {
        SpunOff.Add(asked);

        return new Wire.SpinOffReply { Refused = SpinOffRefusal };
    }

    private Wire.DidItReply AskedToEnd(Wire.ForceConcludeRequest asked)
    {
        Concluded.Add(asked);

        return new Wire.DidItReply { Changed = !AlreadyClosed.Contains(asked.ConversationId) };
    }

    private Wire.DidItReply AskedToStart(Wire.StartRequest asked)
    {
        Started.Add(asked);

        return new Wire.DidItReply { Changed = StartAccepted };
    }

    private Wire.FreezeReply AskedToFreeze(Wire.FreezeRequest asked)
    {
        Frozen.Add(asked);

        return new Wire.FreezeReply { Changed = true, Process = FreezeProcess };
    }

    private Wire.DidItReply AskedToUnfreeze(Wire.AgentRequest asked)
    {
        Unfrozen.Add(asked);

        return new Wire.DidItReply { Changed = true };
    }

    private static Wire.DidItReply Asked(List<Wire.StopRequest> into, Wire.StopRequest asked)
    {
        into.Add(asked);

        return new Wire.DidItReply { Changed = true };
    }

    private Wire.DidItReply Taken(Wire.SpinOffRequest asked)
    {
        Abandoned.Add(asked);

        return new Wire.DidItReply { Changed = true };
    }

    /// <summary>One message as gRPC frames it on the wire: an uncompressed flag, its length, and its bytes.</summary>
    private static byte[] Frame(IMessage message)
    {
        var bytes = message.ToByteArray();
        var framed = new byte[5 + bytes.Length];

        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(1, 4), (uint)bytes.Length);
        bytes.CopyTo(framed, 5);

        return framed;
    }

    /// <summary>A response body that gives its first bytes and then waits, as a held seat's stream does, until the call ends.</summary>
    private sealed class HeldOpen(byte[] first, CancellationToken ends) : Stream
    {
        private int _given;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_given < first.Length)
            {
                var count = Math.Min(buffer.Length, first.Length - _given);
                first.AsMemory(_given, count).CopyTo(buffer);
                _given += count;

                return count;
            }

            using var either = CancellationTokenSource.CreateLinkedTokenSource(ends, cancellationToken);

            try
            {
                await Task.Delay(Timeout.Infinite, either.Token);
            }
            catch (OperationCanceledException)
            {
            }

            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Answers as the broker does: one proposal by its identifier, or none.</summary>
    private Wire.ProposalsReply OneProposal(long id)
    {
        var reply = new Wire.ProposalsReply();

        reply.Proposals.AddRange(Proposals.Where(p => p.Id == id));

        return reply;
    }

    /// <summary>Answers as the broker does: a list, as headings when asked.</summary>
    private Wire.ProposalsReply ListProposals(Wire.ProposalsRequest request)
    {
        var reply = new Wire.ProposalsReply();

        foreach (var proposal in Proposals)
        {
            if (request.Headings)
            {
                var heading = proposal.Clone();
                heading.ItemCount = proposal.Content.Items.Count;
                heading.Content = new Wire.RoadmapContent { Title = proposal.Content.Title };
                reply.Proposals.Add(heading);
            }
            else
            {
                reply.Proposals.Add(proposal);
            }
        }

        return reply;
    }

    private Wire.RoadmapsReply ListRoadmaps()
    {
        var reply = new Wire.RoadmapsReply();

        reply.Roadmaps.AddRange(Roadmaps);

        return reply;
    }

    /// <summary>An empty conversation, carrying the reservation when a roadmap's item holds that name.</summary>
    /// <remarks>
    /// By the item's record, the way the broker answers, and never by the name's shape: a conversation called
    /// <c>rm9-9</c> that no item reserved is an ordinary one.
    /// </remarks>
    private Wire.ExportReply Export(string conversation)
    {
        var reply = new Wire.ExportReply();

        reply.Exchanges.AddRange(ExportExchanges);
        reply.Messages.AddRange(ExportMessages.Where(m => m.ConversationId == conversation));

        foreach (var detail in Details.Values)
        {
            if (detail.Items.FirstOrDefault(i => i.ConversationName == conversation) is { } item)
            {
                reply.Item = new Wire.RoadmapItemRef
                {
                    RoadmapId = detail.Id,
                    RoadmapNumber = detail.Number,
                    Title = detail.Title,
                    ItemNumber = item.Number,
                    Removed = item.RemovedRevision != 0,
                };
            }
        }

        return reply;
    }

    /// <summary>Answers a listing: everything at once, or a page at a time and without the archived when a test asked for that.</summary>
    /// <param name="request">What was asked.</param>
    /// <returns>The conversations, and a token when more follow.</returns>
    private Wire.ListConversationsReply Listed(Wire.ListConversationsRequest request)
    {
        var listed = Conversations.Where(c => !OmitsArchived || request.IncludeArchived || !c.Archived).ToList();

        if (ConversationPageSize <= 0)
        {
            return new Wire.ListConversationsReply { Conversations = { listed } };
        }

        var from = int.TryParse(request.PageToken, NumberStyles.None, CultureInfo.InvariantCulture, out var at) ? at : 0;
        var reply = new Wire.ListConversationsReply { Conversations = { listed.Skip(from).Take(ConversationPageSize) } };

        if (from + ConversationPageSize < listed.Count)
        {
            reply.NextPageToken = (from + ConversationPageSize).ToString(CultureInfo.InvariantCulture);
        }

        return reply;
    }

    private Wire.RoadmapReply One(long id)
    {
        var reply = new Wire.RoadmapReply();

        if (Details.TryGetValue(id, out var detail))
        {
            reply.Roadmap = detail;
        }

        return reply;
    }

    private Wire.RoadmapChangeReply Confirm(Wire.ConfirmProposalRequest request)
    {
        Confirmed.Add(request);

        return Refuse is { } why
            ? new Wire.RoadmapChangeReply { Done = false, Problem = why }
            : new Wire.RoadmapChangeReply { Done = true, RoadmapId = 1, Revision = 1, Origin = Wire.RoadmapOrigin.Edited };
    }

    private Wire.RoadmapChangeReply Decline(long id)
    {
        Declined.Add(id);

        return new Wire.RoadmapChangeReply { Done = true };
    }

    private Wire.RoadmapChangeReply Edit(Wire.EditRoadmapRequest request)
    {
        Edited.Add(request);

        if (Refuse is { } why)
        {
            return new Wire.RoadmapChangeReply { Done = false, Problem = why };
        }

        if (Details.TryGetValue(request.Id, out var detail))
        {
            // The roadmap as the broker would hold it after the edit: kept numbers, new numbers after the highest, and
            // an item left out marked removed.
            var kept = request.Content.Items.Where(i => i.Number != 0).Select(i => i.Number).ToHashSet();
            var next = detail.Items.Select(i => i.Number).DefaultIfEmpty(0).Max() + 1;
            var numbers = request.Content.Items.Select(i => i.Number != 0 ? i.Number : next++).ToList();
            var items = new List<Wire.RoadmapItemDetail>();

            for (var at = 0; at < request.Content.Items.Count; at++)
            {
                var draft = request.Content.Items[at];
                var live = new Wire.RoadmapItemDetail
                {
                    Id = numbers[at],
                    Number = numbers[at],
                    Position = at + 1,
                    Text = draft.Text,
                    IntendedAgent = draft.IntendedAgent,
                    ConversationName = $"rm{detail.Number}-{numbers[at]}",
                    Facts = new Wire.ItemFacts(),
                };

                live.Needs.AddRange(draft.Needs.Select(position => numbers[position - 1]));
                items.Add(live);
            }

            foreach (var gone in detail.Items.Where(i => i.RemovedRevision == 0 && !kept.Contains(i.Number)))
            {
                gone.RemovedRevision = request.Revision + 1;
                gone.Facts = null;
                items.Add(gone);
            }

            detail.Title = request.Content.Title;
            detail.Revision = request.Revision + 1;
            detail.Items.Clear();
            detail.Items.AddRange(items);
        }

        return new Wire.RoadmapChangeReply { Done = true, RoadmapId = request.Id, Revision = request.Revision + 1, Origin = Wire.RoadmapOrigin.Edited };
    }
}
