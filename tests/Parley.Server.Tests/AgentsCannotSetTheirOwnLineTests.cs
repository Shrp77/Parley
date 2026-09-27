using System.Reflection;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That nothing an agent can reach writes what that agent owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the guard SPEC.md §17 question 10 exists for, and it is structural rather than
/// documentary.</b> Question 10 is closed and says the field is not self-declared; 020 amends it to
/// permit a line an agent <em>drafted</em>, on the condition that a person accepts it. An agent that
/// could write the field itself would make the amendment meaningless, and the narrowing question 10
/// records would have been given away for nothing.
/// </para>
/// <para>
/// <b>Reflection over the generated clients rather than a reading of the proto.</b> §10.2 warns that
/// an adapter surfacing an operator operation into an agent's tool set would breach §10.1, and the
/// document's own protection is a naming convention — which depends on whoever writes the adapter
/// reading carefully. What actually holds the line is that <c>Agents</c> and <c>Operators</c> are two
/// services: an adapter binds the first and the first has no such method. That is checkable, so it is
/// checked.
/// </para>
/// <para>
/// <b>Must fail against the operation being added to the wrong service</b>, which is the plausible
/// mistake rather than an exotic one: both services live in one proto file, and adding an rpc to the
/// service above the intended one is a two-line edit that compiles, passes every functional test, and
/// hands every agent the ability to declare its own scope.
/// </para>
/// </remarks>
public sealed class AgentsCannotSetTheirOwnLineTests
{
    /// <summary>What a method or message must not be called on the agent's side.</summary>
    /// <remarks>
    /// Matched on the word rather than on an exact method name, because the defect being guarded is
    /// somebody adding an operation under a name nobody predicted. A test naming only
    /// <c>RecordOwnership</c> would pass against <c>SetOwns</c>.
    /// </remarks>
    private static readonly string[] Forbidden = ["Ownership", "Owns"];

    [Fact]
    public void The_agents_service_has_no_method_that_writes_what_an_agent_owns()
    {
        var offered = typeof(Wire.Agents.AgentsClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Where(name => Forbidden.Any(word => name.Contains(word, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offered);
    }

    [Fact]
    public void No_message_an_agent_sends_carries_an_ownership_statement_to_be_written()
    {
        // The parameter types are the other half: a method named innocuously could still take a
        // request carrying the field. Every message an agent can send is a parameter of some method
        // on its own client, so the set is closed and can be walked.
        var carried = typeof(Wire.Agents.AgentsClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Select(type => type.IsGenericType ? type.GetGenericArguments()[0] : type)
            .Distinct()
            .Where(type => Forbidden.Any(word => type.Name.Contains(word, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(carried);
    }

    [Fact]
    public void The_operators_service_is_where_it_lives()
    {
        // **The positive half, and it is what makes the two above mean something.** Two empty
        // assertions are also satisfied by an operation that does not exist at all, so this pins the
        // operation as present on the other service -- the pair together say it is on exactly one
        // side rather than merely absent from this one.
        var operators = typeof(Wire.Operators.OperatorsClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToList();

        Assert.Contains("RecordOwnership", operators);
    }

    [Fact]
    public void The_adapter_offers_no_tool_that_declares_what_it_owns()
    {
        // The wire is one route and an agent's tool set is the other. §12.3 makes registration the
        // adapter's own act on attach, which is how the line reaches the registry at all -- so what
        // must not exist is a *tool* a model can call to change it, which would be self-declaration
        // with extra steps.
        //
        // Read from the source rather than by reflection, because this project deliberately does not
        // reference Parley.Mcp: an agent's own server is on no path from here, and adding a reference
        // to assert its absence would be the odd choice.
        var tools = Directory
            .EnumerateFiles(Sources("src/Parley.Mcp"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Where(line => line.Contains("McpServerTool(Name", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(tools);

        Assert.DoesNotContain(
            tools,
            line => line.Contains("own", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The repository directory holding a project's sources.</summary>
    /// <param name="project">Its path from the repository root.</param>
    /// <returns>The absolute directory.</returns>
    /// <remarks>
    /// Walked up from the test assembly rather than taken from the working directory, which the test
    /// host does not promise. The marker is the solution file, so this stops at the repository root
    /// and not at whatever directory happens to hold a `src`.
    /// </remarks>
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
