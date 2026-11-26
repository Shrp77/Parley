using System.Globalization;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Net.Client;
using Wire = Parley.Transport;

namespace Parley.Screens;

/// <summary>Plays a scenario into a broker over the wire, as its agents, its node and the person.</summary>
/// <remarks>
/// <para>
/// <b>Through the same calls an adapter, a node and a surface make</b>, never into the store. A screenshot of a state
/// written straight into tables would show what nobody can reach; here a step the broker refuses stops the run and
/// says which step, so the scenario is corrected rather than the screen.
/// </para>
/// <para>
/// The agents and the node stay connected until the tool ends, because whether an agent is running is whether its
/// connection is open. The person's seat is let go once the scenario is played, so the console takes it as it would.
/// </para>
/// </remarks>
internal sealed class Seeder : IAsyncDisposable
{
    private readonly Scenario _scenario;
    private readonly string _folder;
    private readonly GrpcChannel _channel;
    private readonly Wire.Agents.AgentsClient _agents;
    private readonly Wire.Operators.OperatorsClient _operators;
    private readonly Wire.Nodes.NodesClient _nodes;
    private readonly CancellationTokenSource _hold = new();
    private readonly Dictionary<string, AsyncServerStreamingCall<Wire.Event>> _attached = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Id, Wire.RoadmapContent Content)> _proposals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _turnsStarted = new(StringComparer.Ordinal);
    private readonly List<Task> _drains = [];
    private AsyncServerStreamingCall<Wire.NodeCommand>? _node;

    /// <summary>Connects to a broker to play <paramref name="scenario"/> into it.</summary>
    /// <param name="scenario">What to play.</param>
    /// <param name="folder">The scenario's folder, which its file and render paths are relative to.</param>
    /// <param name="broker">The broker's address.</param>
    public Seeder(Scenario scenario, string folder, string broker)
    {
        _scenario = scenario;
        _folder = folder;
        _channel = GrpcChannel.ForAddress(broker);
        _agents = new Wire.Agents.AgentsClient(_channel);
        _operators = new Wire.Operators.OperatorsClient(_channel);
        _nodes = new Wire.Nodes.NodesClient(_channel);
    }

    /// <summary>Plays every step.</summary>
    /// <param name="render">Captures an HTML file as a PNG of the given size, for a placed image the scenario draws.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What later shots refer to by name.</returns>
    /// <exception cref="InvalidOperationException">The broker refused a step; the message says which and why.</exception>
    public async Task<Placeholders> PlayAsync(Func<string, int, int, Task<byte[]>> render, CancellationToken cancellationToken)
    {
        await JoinNodeAsync(cancellationToken).ConfigureAwait(false);

        await AttachAsync(Scenario.Person, launchToken: null, cancellationToken).ConfigureAwait(false);

        foreach (var agent in _scenario.Agents)
        {
            string? token = null;

            if (string.Equals(agent.Started, AgentStart.Node, StringComparison.Ordinal))
            {
                token = Guid.NewGuid().ToString("N");

                _ = await _nodes.ReportLaunchAsync(
                    new Wire.LaunchReport { Machine = _scenario.Machine, Agent = _scenario.NameOf(agent.Role), Token = token },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            await AttachAsync(agent.Role, token, cancellationToken, agent.Owns).ConfigureAwait(false);
        }

        for (var i = 0; i < _scenario.Steps.Count; i++)
        {
            var step = _scenario.Steps[i];

            try
            {
                await PlayAsync(step, render, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception refused) when (refused is RpcException or InvalidOperationException or IOException
                                               or Microsoft.Playwright.PlaywrightException)
            {
                // IOException covers InvalidDataException, which a rendered image that is not opaque throws.
                throw new InvalidOperationException($"step {i + 1} ({step.Do}): {refused.Message.Split('\n')[0]}", refused);
            }
        }

        // Agents that were only ever registered go now, so the broker reads them as not running.
        foreach (var gone in _scenario.Agents.Where(a => string.Equals(a.Started, AgentStart.Gone, StringComparison.Ordinal)))
        {
            await LeaveAsync(gone.Role, cancellationToken).ConfigureAwait(false);
        }

        await LeaveAsync(Scenario.Person, cancellationToken).ConfigureAwait(false);

        return new Placeholders(_scenario, _labels);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _hold.CancelAsync().ConfigureAwait(false);

        foreach (var call in _attached.Values)
        {
            call.Dispose();
        }

        _node?.Dispose();

        try
        {
            await Task.WhenAll(_drains).ConfigureAwait(false);
        }
        catch (Exception ended) when (ended is RpcException or OperationCanceledException)
        {
            // The connections end because they were cancelled; that is the point.
        }

        _channel.Dispose();
        _hold.Dispose();
    }

    private async Task PlayAsync(ScenarioStep step, Func<string, int, int, Task<byte[]>> render, CancellationToken cancellationToken)
    {
        switch (step.Do)
        {
            case StepKind.Request:
                await RequestAsync(step, cancellationToken).ConfigureAwait(false);
                break;

            case StepKind.Clarify:
                Said(await _agents.ClarifyAsync(
                    new Wire.ClarifyRequest
                    {
                        From = _scenario.NameOf(step.From!),
                        ResponderModel = _scenario.ModelOf(step.From!),
                        ExchangeId = ExchangeOf(step.Exchange!),
                        PayloadJson = new JsonObject { ["text"] = step.Text }.ToJsonString(),
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false));
                break;

            case StepKind.Result:
                var outcome = new JsonObject { ["id"] = "request", ["outcome"] = step.Outcome, ["text"] = step.Text };

                if (step.Blocker is not null)
                {
                    outcome["blocker"] = step.Blocker;
                }

                Said(await _agents.ResultAsync(
                    new Wire.ResultRequest
                    {
                        From = _scenario.NameOf(step.From!),
                        ResponderModel = _scenario.ModelOf(step.From!),
                        ExchangeId = ExchangeOf(step.Exchange!),
                        PayloadJson = new JsonObject { ["outcomes"] = new JsonArray(outcome) }.ToJsonString(),
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false));
                break;

            case StepKind.Place:
                await PlaceAsync(step, render, cancellationToken).ConfigureAwait(false);
                break;

            case StepKind.Propose:
                await ProposeAsync(step, cancellationToken).ConfigureAwait(false);
                break;

            case StepKind.Confirm:
                var (id, content) = _proposals[step.Proposal!];
                var confirmed = await _operators.ConfirmProposalAsync(
                    new Wire.ConfirmProposalRequest { Id = id, Content = content },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!confirmed.Done)
                {
                    throw new InvalidOperationException("the broker did not confirm the proposal: " + confirmed.Problem);
                }

                break;

            case StepKind.Freeze:
                _ = await _operators.FreezeAsync(
                    new Wire.FreezeRequest { Agent = _scenario.NameOf(step.Agent!), KeepFrozen = step.Keep },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            case StepKind.Turn:
                await TurnAsync(step, cancellationToken).ConfigureAwait(false);
                break;

            case StepKind.Leave:
                await LeaveAsync(step.Agent!, cancellationToken).ConfigureAwait(false);
                break;

            case StepKind.Describe:
                var described = await _agents.DescribeBackgroundTaskAsync(
                    new Wire.DescribeBackgroundTaskRequest
                    {
                        Agent = _scenario.NameOf(step.Agent!),
                        TaskId = step.Task!,
                        Name = step.Name!,
                        Description = step.Text ?? string.Empty,
                        Link = step.Link ?? string.Empty,
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                // A shot that shows a task's words must not quietly show the runner's instead.
                if (!described.Kept || described.LinkDropped.Length > 0)
                {
                    throw new InvalidOperationException($"the broker did not keep {step.Task}'s words: {described.Refused}{described.LinkDropped}");
                }

                break;

            default:
                throw new InvalidOperationException($"'{step.Do}' is not a step this tool knows");
        }
    }

    private async Task RequestAsync(ScenarioStep step, CancellationToken cancellationToken)
    {
        var reply = await _agents.RequestAsync(
            new Wire.OpenRequest
            {
                From = _scenario.NameOf(step.From!),
                ResponderModel = _scenario.ModelOf(step.From!),
                To = _scenario.NameOf(step.To!),
                ConversationId = step.Conversation ?? string.Empty,
                PayloadJson = new JsonObject { ["ask"] = step.Text }.ToJsonString(),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var outcome = reply.Outcomes.Single();

        if (!outcome.Ok)
        {
            throw new InvalidOperationException($"the broker refused the request: {outcome.Rejection}");
        }

        if (step.Expect is { } expected && outcome.ExchangeId != expected)
        {
            throw new InvalidOperationException(
                $"the request became exchange #{outcome.ExchangeId}, and the scenario expects #{expected}. Add or remove "
                + "earlier requests so the numbers the shots name stay true.");
        }

        if (step.Label is { } label)
        {
            _labels[label] = outcome.ExchangeId;
        }
    }

    private async Task PlaceAsync(ScenarioStep step, Func<string, int, int, Task<byte[]>> render, CancellationToken cancellationToken)
    {
        // Judged inside the folder at load; judged again here, because this is where the bytes are read.
        var source = Scenario.Inside(_folder, step.Render ?? step.File!)
            ?? throw new InvalidOperationException($"'{step.Render ?? step.File}' is outside the round's folder");

        var bytes = step.Render is not null
            ? await render(source, step.Width, step.Height).ConfigureAwait(false)
            : await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);

        var header = new Wire.PlaceFileHeader { Name = step.Name!, ConversationId = step.Conversation ?? string.Empty };
        Wire.PlaceFileReply placed;

        using (var content = new MemoryStream(bytes, writable: false))
        {
            if (string.Equals(step.By, Scenario.Person, StringComparison.Ordinal))
            {
                using var call = _operators.PlacePersonsFile(cancellationToken: cancellationToken);
                placed = await call.PlaceAsync(header, content, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                header.Agent = _scenario.NameOf(step.By!);

                using var call = _agents.PlaceFile(cancellationToken: cancellationToken);
                placed = await call.PlaceAsync(header, content, cancellationToken).ConfigureAwait(false);
            }
        }

        if (placed.Outcome is not (Wire.PlaceOutcome.Placed or Wire.PlaceOutcome.AlreadyCurrent))
        {
            throw new InvalidOperationException($"the broker did not place {step.Name}: {placed.Outcome} {placed.Rejection} {placed.Detail}");
        }
    }

    private async Task ProposeAsync(ScenarioStep step, CancellationToken cancellationToken)
    {
        var content = new Wire.RoadmapContent { Title = step.Title! };

        content.Items.Add(step.Items!.Select(text => new Wire.RoadmapItemDraft { Text = text }));

        var proposed = await _agents.ProposeRoadmapAsync(
            new Wire.ProposeRoadmapRequest
            {
                Agent = _scenario.NameOf(step.By!),
                Content = content,
                ConversationId = step.Conversation ?? string.Empty,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (proposed.ProposalId == 0)
        {
            throw new InvalidOperationException("the broker refused the proposal: " + proposed.Problem);
        }

        _proposals[step.Label!] = (proposed.ProposalId, content);
    }

    private async Task TurnAsync(ScenarioStep step, CancellationToken cancellationToken)
    {
        var agent = _scenario.NameOf(step.Agent!);
        var report = new Wire.TurnReport { Machine = _scenario.Machine, Agent = agent };

        switch (step.Phase)
        {
            case "working":
                var started = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                _turnsStarted[agent] = started;
                report.Started = new Wire.TurnStarted { StartedUtc = started };
                break;

            case "ended":
                report.Ended = new Wire.TurnEnded
                {
                    StartedUtc = _turnsStarted.GetValueOrDefault(agent) ?? throw new InvalidOperationException("no turn was started"),
                    Clean = true,
                };
                break;

            default:
                report.Background = new Wire.BackgroundWork { Tasks = step.Tasks };
                report.Background.Running.AddRange((step.Labels ?? []).Select((label, i) => new Wire.RunningTask
                {
                    Id = "t" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    Kind = Wire.TaskKind.Agent,
                    Label = label,
                }));
                break;
        }

        _ = await _nodes.ReportTurnAsync(report, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task JoinNodeAsync(CancellationToken cancellationToken)
    {
        var join = new Wire.JoinRequest
        {
            Machine = _scenario.Machine,
            ReportsTurns = true,
            ProtocolVersion = Parley.Core.ProtocolVersion.Current,
            Facts = new Wire.NodeFacts { Os = "Windows", OsVersion = "11" },
        };

        join.StartableRoles.Add(_scenario.Agents
            .Where(a => string.Equals(a.Started, AgentStart.Node, StringComparison.Ordinal))
            .Select(a => a.Role));

        _node = _nodes.Join(join, cancellationToken: _hold.Token);

        // The node never acts on what it is asked: the scenario says what each agent is doing, and a start or a stop
        // the broker asks for would change it. It reads its commands only so the connection stays open.
        _drains.Add(DrainAsync(_node.ResponseStream));

        // The join is a stream, so nothing returns when it is in place; the broker says so when asked about the machine.
        for (var tries = 0; tries < 50; tries++)
        {
            try
            {
                var described = await _nodes.DescribeAsync(new Wire.DescribeNodeRequest { Machine = _scenario.Machine }, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (described.Connected)
                {
                    return;
                }
            }
            catch (RpcException unknown) when (unknown.StatusCode == StatusCode.NotFound)
            {
                // Not heard from yet: the join is still on its way.
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"the node {_scenario.Machine} did not join");
    }

    private async Task AttachAsync(string who, string? launchToken, CancellationToken cancellationToken, string? owns = null)
    {
        var person = string.Equals(who, Scenario.Person, StringComparison.Ordinal);
        var call = _agents.Attach(
            new Wire.AttachRequest
            {
                Agent = _scenario.NameOf(who),
                Adapter = person ? "parley-console" : "mcp",
                Tier = Wire.AgentTier.Pull,
                Owns = owns ?? string.Empty,
                ProtocolVersion = Parley.Core.ProtocolVersion.Current,
                LaunchToken = launchToken ?? string.Empty,
            },
            cancellationToken: _hold.Token);

        if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)
            || call.ResponseStream.Current.EventCase != Wire.Event.EventOneofCase.Attached)
        {
            call.Dispose();
            throw new InvalidOperationException($"{_scenario.NameOf(who)} was not attached");
        }

        _attached[who] = call;
        _drains.Add(DrainAsync(call.ResponseStream));
    }

    /// <summary>Closes a participant's connection and waits until the broker reads it as gone.</summary>
    /// <remarks>
    /// The broker learns of a closed stream on its own time, so a shot taken straight after could still show the agent
    /// running. Attaching is confirmed by its first event and the node's join by a describe; leaving by the registry.
    /// </remarks>
    private async Task LeaveAsync(string who, CancellationToken cancellationToken)
    {
        if (!_attached.Remove(who, out var call))
        {
            return;
        }

        call.Dispose();

        var name = _scenario.NameOf(who);

        for (var tries = 0; tries < 100; tries++)
        {
            var registry = await _operators.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!registry.Rows.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal) && r.Connected))
            {
                return;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"{name} still reads as connected after it left");
    }

    private long ExchangeOf(string reference) =>
        _labels.TryGetValue(reference, out var id) ? id : long.Parse(reference, CultureInfo.InvariantCulture);

    private static void Said(Wire.SayReply reply)
    {
        if (!reply.Ok)
        {
            throw new InvalidOperationException($"the broker refused it: {reply.Refusal}");
        }
    }

    private static async Task DrainAsync<T>(IAsyncStreamReader<T> stream)
    {
        try
        {
            while (await stream.MoveNext(CancellationToken.None).ConfigureAwait(false))
            {
            }
        }
        catch (RpcException)
        {
            // Ended by this tool letting go, or by the broker stopping first.
        }
    }
}
