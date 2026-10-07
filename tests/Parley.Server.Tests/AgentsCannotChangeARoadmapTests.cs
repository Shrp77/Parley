using System.Reflection;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the one thing an agent can do with a roadmap is propose one (SPEC.md §5.7, principle 2.12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Structural, as <see cref="AgentsCannotSetTheirOwnLineTests"/> is.</b> A roadmap is the person's plan, and an
/// agent that could read, change, reorder, remove from or mark done one would be an agent deciding what other agents
/// are asked to do, which principle 2.12 and principle 2.5 exist to refuse. The line is held by the shape of the
/// services: an adapter binds <c>Agents</c> and <c>Agents</c> has exactly one roadmap method, which stores a proposal.
/// </para>
/// <para>
/// <b>Matched on words, and the allowed set is closed.</b> The mistake being guarded is an operation added under a
/// name nobody predicted. A test naming only <c>EditRoadmap</c> would pass against <c>SetPlan</c>, so the rule is
/// the other way round: nothing on the agents' side may mention a roadmap or a proposal except the named few.
/// </para>
/// <para>
/// <b>Reachability as well as names.</b> A method named innocuously could still take or return a message that carries
/// a roadmap, so every message reachable from an <c>Agents</c> method is walked.
/// </para>
/// </remarks>
public sealed class AgentsCannotChangeARoadmapTests
{
    private static readonly string[] Words = ["Roadmap", "Proposal", "Proposals"];

    /// <summary>The only agent-side members that may mention a roadmap, written down so adding one is a decision.</summary>
    private static readonly HashSet<string> Allowed =
    [
        // The operation itself.
        "ProposeRoadmap", "ProposeRoadmapAsync",

        // What it takes and returns, and what an agent is told about its own proposal in its inbox.
        "ProposeRoadmapRequest", "ProposeRoadmapReply", "RoadmapContent", "RoadmapItemDraft", "RoadmapNotice",

        // The properties that carry those: the proposal's own content and the inbox's notices and counts.
        "Content", "RoadmapNotices", "RoadmapNoticesWaiting", "ProposalId", "ConversationId",
    ];

    private static bool Mentions(string name) => Words.Any(w => name.Contains(w, StringComparison.Ordinal));

    private static IEnumerable<MethodInfo> AgentMethods() =>
        typeof(Wire.Agents.AgentsClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    private static bool IsMessage(Type type) => type.GetInterfaces().Any(i => i.Name == "IMessage");

    /// <summary>Every message type reachable from a type through its properties.</summary>
    private static IEnumerable<Type> Reachable(Type root, HashSet<Type>? seen = null)
    {
        seen ??= [];

        if (!IsMessage(root) || !seen.Add(root))
        {
            yield break;
        }

        yield return root;

        foreach (var property in root.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var type = property.PropertyType;

            if (type.IsGenericType)
            {
                type = type.GetGenericArguments()[0];
            }

            foreach (var inner in Reachable(type, seen))
            {
                yield return inner;
            }
        }
    }

    [Fact]
    public void The_agents_service_has_one_roadmap_method_and_it_proposes()
    {
        var offered = AgentMethods()
            .Select(method => method.Name)
            .Where(Mentions)
            .Where(name => !Allowed.Contains(name))
            .ToList();

        Assert.Empty(offered);
    }

    [Fact]
    public void Nothing_an_agent_sends_or_receives_reaches_a_roadmap_beyond_what_it_proposed_and_what_became_of_it()
    {
        var types = AgentMethods()
            .SelectMany(method => method.GetParameters().Select(p => p.ParameterType)
                .Append(method.ReturnType))
            .Select(type => type.IsGenericType ? type.GetGenericArguments()[0] : type)
            .SelectMany(type => Reachable(type))
            .Distinct()
            .ToList();

        Assert.NotEmpty(types);

        var reached = types
            .Where(type => Mentions(type.Name))
            .Where(type => !Allowed.Contains(type.Name))
            .Select(type => type.Name)
            .ToList();

        Assert.Empty(reached);

        // The members of those messages too: a roadmap's items or revision inside an innocuous reply.
        var members = types
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (Type: type.Name, p.Name)))
            .Where(m => Mentions(m.Name))
            .Where(m => !Allowed.Contains(m.Name))
            .Select(m => $"{m.Type}.{m.Name}")
            .ToList();

        Assert.Empty(members);
    }

    [Fact]
    public void The_one_thing_an_agent_can_do_is_present_so_that_the_two_checks_above_mean_something()
    {
        // Two empty assertions are also satisfied by an operation that does not exist at all, so this pins the
        // proposal as present on the agents' side and everything else as present on the other.
        var agents = AgentMethods().Select(m => m.Name).ToList();

        Assert.Contains("ProposeRoadmapAsync", agents);
    }

    [Theory]
    [InlineData("Roadmaps")]
    [InlineData("Roadmap")]
    [InlineData("Proposals")]
    [InlineData("ConfirmProposal")]
    [InlineData("DeclineProposal")]
    [InlineData("EditRoadmap")]
    public void The_rest_is_the_persons_and_lives_on_the_operators_service(string operation)
    {
        var operators = typeof(Wire.Operators.OperatorsClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToList();

        Assert.Contains(operation, operators);
        Assert.DoesNotContain(operation, AgentMethods().Select(m => m.Name));
    }

    [Fact]
    public void The_adapter_offers_no_tool_that_reads_or_changes_a_roadmap()
    {
        // The wire is one route and an agent's tool set is the other. The only tool that may mention a roadmap
        // is the one that proposes. Read from the source for the reason the sibling test does: this project
        // deliberately does not reference Parley.Mcp.
        var tools = Directory
            .EnumerateFiles(Sources("src/Parley.Mcp"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Where(line => line.Contains("McpServerTool(Name", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(tools);

        var offered = tools
            .Where(line => line.Contains("roadmap", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("proposal", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Contains("parley_propose_roadmap", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offered);
    }

    private static string Sources(string project)
    {
        var at = new DirectoryInfo(AppContext.BaseDirectory);

        while (at is not null && !File.Exists(Path.Combine(at.FullName, "Parley.slnx")))
        {
            at = at.Parent;
        }

        Assert.NotNull(at);

        var sources = Path.Combine(at.FullName, project);

        Assert.True(Directory.Exists(sources), $"'{sources}' is not a directory.");

        return sources;
    }
}
