using System.Text.Json;
using System.Text.Json.Serialization;
using Parley.Core;

namespace Parley.Screens;

/// <summary>The sample deployment a round of screenshots shows: who takes part, and what they said, in order.</summary>
/// <param name="Project">The project's name.</param>
/// <param name="Machine">The machine every agent runs on, the half of each name after the <c>@</c>.</param>
/// <param name="Agents">Who takes part, besides the person.</param>
/// <param name="Steps">What happens, in order.</param>
/// <param name="Description">The project's description, or null.</param>
/// <remarks>
/// <para>
/// <b>A file, not code, because it changes every round and the code should not.</b> The site's agent sends what each
/// shot must show; the scenario is that, written as acts a broker understands. Every act goes over the wire exactly as
/// an agent's or the person's would, so a screen shows what the broker really makes of it, and a state the broker
/// cannot reach fails the run instead of being painted.
/// </para>
/// <para>
/// Everything that can be judged from the file alone is judged at load, before a browser or a broker starts: unknown
/// and missing members, names outside Parley's grammar, a step missing what its kind needs, a label used before it is
/// made or by the wrong kind of step, and a file outside the round's folder.
/// </para>
/// </remarks>
internal sealed record Scenario(
    string Project,
    string Machine,
    IReadOnlyList<ScenarioAgent> Agents,
    IReadOnlyList<ScenarioStep> Steps,
    string? Description = null)
{
    /// <summary>The word a scenario uses for the person.</summary>
    public const string Person = "person";

    /// <summary>Gets the labels of request steps, which name exchanges.</summary>
    [JsonIgnore]
    public IReadOnlySet<string> ExchangeLabels { get; private init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Reads a scenario, refusing anything it does not understand.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The scenario.</returns>
    /// <exception cref="InvalidDataException">The file is not a usable scenario; the message says why.</exception>
    public static Scenario Load(string path)
    {
        var scenario = Json.Read<Scenario>(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var problems = scenario.Problems(folder).ToList();

        return problems.Count == 0
            ? scenario with
            {
                ExchangeLabels = scenario.Steps
                    .Where(s => s.Do == StepKind.Request && s.Label is not null)
                    .Select(s => s.Label!)
                    .ToHashSet(StringComparer.Ordinal),
            }
            : throw new InvalidDataException($"{path}:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", problems));
    }

    /// <summary>The whole name a scenario's short name stands for.</summary>
    /// <param name="who"><see cref="Person"/>, or an agent's role.</param>
    /// <returns><c>human-user@project</c> for the person, otherwise <c>role@machine</c>.</returns>
    public string NameOf(string who) =>
        string.Equals(who, Person, StringComparison.Ordinal) ? $"human-user@{Project}" : $"{who}@{Machine}";

    /// <summary>Whether a short name is the person or one of the scenario's agents.</summary>
    /// <param name="who">The short name.</param>
    /// <returns><see langword="true"/> when the scenario knows it.</returns>
    public bool Knows(string who) =>
        string.Equals(who, Person, StringComparison.Ordinal) || Agents.Any(a => string.Equals(a.Role, who, StringComparison.Ordinal));

    /// <summary>The model a participant's messages name, as its adapter or the person's seat would send it.</summary>
    /// <param name="who"><see cref="Person"/>, or an agent's role.</param>
    /// <returns><c>human-user</c> for the person, as a seat says; the agent's model, or <c>unknown-model</c> as an adapter says when none is set.</returns>
    public string ModelOf(string who) =>
        string.Equals(who, Person, StringComparison.Ordinal)
            ? "human-user"
            : Agents.FirstOrDefault(a => string.Equals(a.Role, who, StringComparison.Ordinal))?.Model ?? "unknown-model";

    /// <summary>Resolves a file a step names inside the round's folder.</summary>
    /// <param name="folder">The round's folder.</param>
    /// <param name="relative">The path the step gives.</param>
    /// <returns>The full path, or null when it leaves the folder.</returns>
    /// <remarks>
    /// A placed file's bytes end up in published screenshots (the Files and Media pages draw them), so a round may only
    /// place what is in its own folder, where it is reviewed with the round.
    /// </remarks>
    public static string? Inside(string folder, string relative)
    {
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private IEnumerable<string> Problems(string folder)
    {
        if (!ProjectName.IsValid(Project))
        {
            yield return $"project '{Project}' is not a name Parley accepts";
        }

        if (!AgentName.TryCreate($"{Person}@{Machine}", out _))
        {
            yield return $"machine '{Machine}' is not a name Parley accepts";
        }

        foreach (var agent in Agents)
        {
            if (string.Equals(agent.Role, Person, StringComparison.Ordinal) || !AgentName.TryCreate(NameOf(agent.Role), out _))
            {
                yield return $"agent '{agent.Role}' is not a role a scenario can use";
            }

            if (agent.Started is not (AgentStart.Node or AgentStart.Outside or AgentStart.Gone))
            {
                yield return $"agent {agent.Role}: started must be node, outside or gone";
            }
        }

        foreach (var twice in Agents.GroupBy(a => a.Role, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            yield return $"agent {twice.Key} is listed twice";
        }

        var exchanges = new HashSet<string>(StringComparer.Ordinal);
        var proposals = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            var at = $"step {i + 1} ({step.Do})";

            foreach (var missing in step.Missing())
            {
                yield return $"{at}: {missing}";
            }

            foreach (var who in new[] { step.From, step.To, step.By, step.Agent }.OfType<string>())
            {
                if (!Knows(who))
                {
                    yield return $"{at}: '{who}' is neither an agent nor the person";
                }
            }

            if (step.Agent is { } subject && step.Do is StepKind.Freeze or StepKind.Turn or StepKind.Leave
                && string.Equals(subject, Person, StringComparison.Ordinal))
            {
                yield return $"{at}: the person cannot be frozen, given turns or made to leave";
            }

            if (step.Exchange is { } reference && !exchanges.Contains(reference) && !long.TryParse(reference, out _))
            {
                yield return $"{at}: exchange '{reference}' is no earlier request's label";
            }

            if (step.Proposal is { } proposal && !proposals.Contains(proposal))
            {
                yield return $"{at}: proposal '{proposal}' is no earlier propose step's label";
            }

            foreach (var file in new[] { step.File, step.Render }.OfType<string>())
            {
                if (Inside(folder, file) is not { } full)
                {
                    yield return $"{at}: '{file}' is outside the round's folder";
                }
                else if (!System.IO.File.Exists(full))
                {
                    yield return $"{at}: '{file}' does not exist";
                }
            }

            if (step.Label is { } label)
            {
                var labels = step.Do switch
                {
                    StepKind.Request => exchanges,
                    StepKind.Propose => proposals,
                    _ => null,
                };

                if (labels is null)
                {
                    yield return $"{at}: only a request or a propose step takes a label";
                }
                else if (exchanges.Contains(label) || proposals.Contains(label) || !labels.Add(label))
                {
                    yield return $"{at}: label '{label}' is used twice";
                }
            }

            if (step.Expect is not null && step.Do != StepKind.Request)
            {
                yield return $"{at}: only a request takes expect";
            }
        }
    }
}

/// <summary>How an agent came to be running.</summary>
internal static class AgentStart
{
    /// <summary>Started by the scenario's node, which can stop, restart and give it turns.</summary>
    public const string Node = "node";

    /// <summary>Opened by somebody outside Parley: attached with no launch token.</summary>
    public const string Outside = "outside";

    /// <summary>Attached once to be registered, then gone.</summary>
    public const string Gone = "gone";
}

/// <summary>The kinds of step a scenario may hold: the value of a step's <c>do</c>.</summary>
internal static class StepKind
{
    /// <summary>A request from an agent or the person.</summary>
    public const string Request = "request";

    /// <summary>A clarify on an exchange.</summary>
    public const string Clarify = "clarify";

    /// <summary>A result that closes an exchange.</summary>
    public const string Result = "result";

    /// <summary>A placed file.</summary>
    public const string Place = "place";

    /// <summary>A roadmap an agent proposes.</summary>
    public const string Propose = "propose";

    /// <summary>The person confirming a proposal.</summary>
    public const string Confirm = "confirm";

    /// <summary>The person freezing an agent.</summary>
    public const string Freeze = "freeze";

    /// <summary>The node reporting a turn.</summary>
    public const string Turn = "turn";

    /// <summary>An agent's connection closing.</summary>
    public const string Leave = "leave";
}

/// <summary>One participant besides the person.</summary>
/// <param name="Role">Its role, the half of its name before the <c>@</c>.</param>
/// <param name="Owns">What it owns, as its launch environment would say.</param>
/// <param name="Started"><c>node</c>, <c>outside</c> or <c>gone</c>; see <see cref="AgentStart"/>.</param>
/// <param name="Model">The model its messages say produced them, or null for the adapter's own default.</param>
internal sealed record ScenarioAgent(string Role, string Owns, string Started, string? Model = null);

/// <summary>One act in a scenario. Which members it needs depends on <see cref="Do"/>.</summary>
/// <param name="Do">One of <see cref="StepKind"/>'s values.</param>
/// <param name="From">Who speaks, for request, clarify and result.</param>
/// <param name="To">Who a request is for.</param>
/// <param name="By">Who places a file or proposes a roadmap.</param>
/// <param name="Agent">The agent a freeze, a turn or leaving is about.</param>
/// <param name="Conversation">The conversation, for a request, a placement or a proposal.</param>
/// <param name="Exchange">The exchange a clarify or result is on: an earlier request's label, or a number.</param>
/// <param name="Proposal">The proposal a confirm accepts: an earlier propose step's label.</param>
/// <param name="Text">What is said.</param>
/// <param name="Outcome">A result's outcome: done, answered, acknowledged or blocked.</param>
/// <param name="Blocker">A blocked result's blocker.</param>
/// <param name="Name">A placed file's name.</param>
/// <param name="File">A file to place, relative to the scenario.</param>
/// <param name="Render">An HTML file, relative to the scenario, captured in the browser as the placed image.</param>
/// <param name="Width">The rendered image's width.</param>
/// <param name="Height">The rendered image's height.</param>
/// <param name="Title">A roadmap's title.</param>
/// <param name="Items">A roadmap's items, one line each.</param>
/// <param name="Keep">Whether a freeze outlasts the broker's next start.</param>
/// <param name="Phase">A turn's phase: <c>working</c>, <c>ended</c> or <c>background</c>.</param>
/// <param name="Tasks">How many background tasks, for the background phase.</param>
/// <param name="Label">A name later steps and shots use for what a request or a proposal made.</param>
/// <param name="Expect">
/// The exchange number a request must get. Set it only where a shot or the site's words name the number: the run stops
/// if the broker numbers it otherwise, so those words stay true.
/// </param>
internal sealed record ScenarioStep(
    string Do,
    string? From = null,
    string? To = null,
    string? By = null,
    string? Agent = null,
    string? Conversation = null,
    string? Exchange = null,
    string? Proposal = null,
    string? Text = null,
    string? Outcome = null,
    string? Blocker = null,
    string? Name = null,
    string? File = null,
    string? Render = null,
    int Width = 0,
    int Height = 0,
    string? Title = null,
    IReadOnlyList<string>? Items = null,
    bool Keep = false,
    string? Phase = null,
    int Tasks = 0,
    string? Label = null,
    long? Expect = null)
{
    /// <summary>Says which members this kind of step needs and does not have.</summary>
    /// <returns>One line per missing member.</returns>
    public IEnumerable<string> Missing()
    {
        string?[] needed = Do switch
        {
            StepKind.Request => [Need(From, "from"), Need(To, "to"), Need(Text, "text")],
            StepKind.Clarify => [Need(From, "from"), Need(Exchange, "exchange"), Need(Text, "text")],
            StepKind.Result => [Need(From, "from"), Need(Exchange, "exchange"), Need(Text, "text"),
                                Outcome is "done" or "answered" or "acknowledged" or "blocked" ? null : "outcome must be done, answered, acknowledged or blocked",
                                Outcome == "blocked" && Blocker is null ? "a blocked result needs blocker" : null],
            StepKind.Place => [Need(By, "by"), Need(Name, "name"), (File is null) == (Render is null) ? "needs file or render, not both" : null,
                               Render is not null && (Width is <= 0 or > 4000 || Height is <= 0 or > 4000) ? "render needs width and height, 1 to 4000" : null],
            StepKind.Propose => [Need(By, "by"), Need(Title, "title"), Items is { Count: > 0 } ? null : "needs items", Need(Label, "label")],
            StepKind.Confirm => [Need(Proposal, "proposal")],
            StepKind.Freeze or StepKind.Leave => [Need(Agent, "agent")],
            StepKind.Turn => [Need(Agent, "agent"), Phase is "working" or "ended" or "background" ? null : "phase must be working, ended or background"],
            _ => [$"'{Do}' is not a step this tool knows"],
        };

        return needed.OfType<string>();

        static string? Need(string? value, string member) => string.IsNullOrWhiteSpace(value) ? $"needs {member}" : null;
    }
}

/// <summary>The one way this tool reads its JSON: members as written in camel case, and nothing it does not know.</summary>
internal static class Json
{
    /// <summary>The options every input is read with.</summary>
    /// <remarks>
    /// A member that is misspelt is refused, and so is one that is left out where it is not optional, so either is a
    /// line in the load's report rather than a null somewhere later. A member written twice is refused too: the reader
    /// would otherwise keep the second and drop the first without a word, which is how a shot once lost its storage.
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads a file as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">What the file holds.</typeparam>
    /// <param name="path">The file.</param>
    /// <returns>What it holds.</returns>
    /// <exception cref="InvalidDataException">The file does not read as one; the message names the file.</exception>
    public static T Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(path), Options)
                ?? throw new InvalidDataException($"{path}: empty");
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException($"{path}: {failure.Message}", failure);
        }
    }
}
