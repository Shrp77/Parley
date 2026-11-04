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

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<BrokerDirectory>();

            services.AddSingleton(_ => new BrokerDirectory(
                [new ProjectSettings(ProjectName.Create(Project), "alpha.db", "http://127.0.0.1:5407", null)],
                address => GrpcChannel.ForAddress(
                    address, new GrpcChannelOptions { HttpHandler = Broker, DisposeHttpClient = false })));
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

    /// <summary>The agents the registry lists, none by default.</summary>
    public List<Wire.RegistryRow> Registry { get; } = [];

    /// <summary>Every confirmation the broker was asked for.</summary>
    public List<Wire.ConfirmProposalRequest> Confirmed { get; } = [];

    /// <summary>Every proposal it was asked to decline.</summary>
    public List<long> Declined { get; } = [];

    /// <summary>Every edit it was asked for.</summary>
    public List<Wire.EditRoadmapRequest> Edited { get; } = [];

    /// <summary>Exchanges every export carries, as when retention has removed a conversation's messages and kept them.</summary>
    public List<Wire.ExchangeSnapshot> ExportExchanges { get; } = [];

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

    /// <summary>When set with <see cref="Seats"/>, the seat's stream says, after the attach, that the store is being rewritten.</summary>
    public bool Tidying { get; set; }

    /// <summary>What a spin-off answers, when it should refuse.</summary>
    public string SpinOffRefusal { get; set; } = string.Empty;

    /// <summary>The end of the one operation's path that cannot reach the broker, when one should fail.</summary>
    public string FailsOn { get; set; } = string.Empty;

    /// <summary>Every operation asked of this broker, by its path.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Paths { get; } = new();

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
                "/parley.v1.Operators/DescribeWaiting" => new Wire.WaitingReply(),
                "/parley.v1.Operators/ListConversations" => new Wire.ListConversationsReply { Conversations = { Conversations } },
                "/parley.v1.Operators/Export" => Export(Wire.ExportRequest.Parser.ParseFrom(payload).ConversationId),
                "/parley.v1.Operators/Implements" => new Wire.ImplementsReply { ProtocolVersion = Parley.Core.ProtocolVersion.Current },
                "/parley.v1.Operators/SpinOff" => Spun(Wire.SpinOffRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Operators/AbandonSpinOff" => Taken(Wire.SpinOffRequest.Parser.ParseFrom(payload)),
                "/parley.v1.Agents/Request" when Seats && RefusesRequestsTo.Contains(Requested[^1].To) => new Wire.RequestReply
                {
                    Outcomes = { new Wire.RequestOutcome { Ok = false, Rejection = Wire.RequestRejection.Duplicate } },
                },
                "/parley.v1.Agents/Request" when Seats => new Wire.RequestReply
                {
                    Outcomes = { new Wire.RequestOutcome { Ok = true, ExchangeId = 7, ConversationId = "rm1-1" } },
                },
                "/parley.v1.Agents/Waiting" when Seats => new Wire.Notice(),
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

        // 12 is UNIMPLEMENTED.
        response.TrailingHeaders.Add("grpc-status", (reply is null ? 12 : 0).ToString(CultureInfo.InvariantCulture));

        return response;
    }

    private Wire.SpinOffReply Spun(Wire.SpinOffRequest asked)
    {
        SpunOff.Add(asked);

        return new Wire.SpinOffReply { Refused = SpinOffRefusal };
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
