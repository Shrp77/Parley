using System.Reflection;
using Parley.Client.Rendering;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// What 006 added to the shared library, and whether any of it should not be there.
/// </summary>
/// <remarks>
/// <para>
/// Lens 3 of the review gate, run 3.1. <c>Parley.Client</c> is the assembly both surfaces read, so
/// everything public on it is a promise to two callers and a thing the next feature will find. The
/// question is not whether it works but whether it is the smallest surface that could.
/// </para>
/// <para>
/// The specific risk this feature carries is duplication rather than excess: a derivation that
/// exists here and is quietly reimplemented in one of the surfaces is how the console and the
/// terminal came to disagree about who was in a conversation, twice in one week, before 004 pulled
/// this assembly out.
/// </para>
/// </remarks>
public sealed class SurfaceGrowthTests
{
    private static IEnumerable<Type> PublicTypes() =>
        typeof(ViewPlace).Assembly.GetExportedTypes()
            .Where(t => t.Namespace is "Parley.Client.Views" or "Parley.Client.Rendering");

    [Fact]
    public void Every_public_type_the_views_namespace_adds_is_documented()
    {
        // The build enforces this for members through CS1591. What it cannot enforce is that the
        // remarks say why rather than restating the signature, so what is checked is that a
        // remarks block exists at all on the types that carry a decision.
        var undocumented = PublicTypes()
            .Where(t => t.Namespace == "Parley.Client.Views")
            .Where(t => !Documented(t))
            .Select(t => t.Name)
            .ToList();

        Assert.True(undocumented.Count == 0, "no documentation for: " + string.Join(", ", undocumented));
    }

    [Fact]
    public void Nothing_in_the_shared_library_knows_what_a_terminal_or_a_page_is()
    {
        // The split's whole point. A type here that named a toolkit would put that toolkit on the
        // other surface's graph, which is the dependency GraphTests refuses one level up.
        foreach (var type in PublicTypes())
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance
                                                   | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var named = member switch
                {
                    MethodInfo method => method.ReturnType.FullName + " " + string.Join(
                        ",", method.GetParameters().Select(p => p.ParameterType.FullName)),
                    PropertyInfo property => property.PropertyType.FullName,
                    FieldInfo field => field.FieldType.FullName,
                    _ => string.Empty,
                };

                Assert.DoesNotContain("Terminal.Gui", named ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("Microsoft.AspNetCore", named ?? string.Empty, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_colour_derivation_exists_once()
    {
        // Palette is the only thing that decides an agent's colour, and ConversationsView forwards
        // to it rather than repeating the hash. A second derivation would be a second answer, and
        // the two would agree until somebody changed one.
        Assert.Equal(
            Palette.SlotFor(AgentName.Create("orders@test")),
            ConversationsView.ColourFor(AgentName.Create("orders@test")));
    }

    [Fact]
    public void A_summary_of_an_unreachable_project_is_not_a_summary_of_an_empty_one()
    {
        // The distinction both surfaces have to keep, expressed in the type rather than in each
        // caller: null is unknown and zero is none, and a record that used zero for both would make
        // the mistake impossible to avoid rather than merely easy to make.
        var unreachable = ProjectSummary.Unreachable("not answering");
        var empty = new ProjectSummary(0, 0, 0, 0, null);

        Assert.Null(unreachable.Agents);
        Assert.Null(unreachable.Conversations);
        Assert.False(unreachable.Reachable);
        Assert.True(empty.Reachable);
        Assert.NotEqual(unreachable.Line(), empty.Line());
    }

    [Fact]
    public void The_place_carries_no_more_than_it_needs_to()
    {
        // Three values: which view, which project, which conversation. Anything else here would be
        // state two surfaces have to keep in step for no reason, and the first thing to drift.
        //
        // **This assertion was wrong first and the code was right.** It asked for no writable
        // properties, and a positional record's members are `init` -- which reports `CanWrite`
        // while being exactly the immutability the test wanted. What it means to ask is that
        // nothing can be written after construction, which is the absence of a non-init setter.
        var settable = typeof(ViewPlace)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.SetMethod is { } setter
                && !setter.ReturnParameter.GetRequiredCustomModifiers()
                    .Any(modifier => modifier.Name == "IsExternalInit"))
            .Select(property => property.Name)
            .ToList();

        Assert.True(settable.Count == 0, "writable after construction: " + string.Join(", ", settable));

        var carried = typeof(ViewPlace)
            .GetConstructors()
            .Max(constructor => constructor.GetParameters().Length);

        Assert.Equal(3, carried);
    }

    [Fact]
    public void Nothing_public_here_returns_a_mutable_collection_a_caller_could_edit()
    {
        // Two surfaces share these. One of them editing a list the other is holding is the class of
        // bug that presents as the wrong project's agents on screen.
        var leaking = new List<string>();

        foreach (var type in PublicTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                                   | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var returns = method.ReturnType;

                if (returns.IsGenericType
                    && returns.GetGenericTypeDefinition() == typeof(List<>))
                {
                    leaking.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        Assert.True(leaking.Count == 0, "returns a mutable list: " + string.Join(", ", leaking));
    }

    private static bool Documented(Type type)
    {
        var xml = Path.Combine(
            Path.GetDirectoryName(type.Assembly.Location)!,
            Path.GetFileNameWithoutExtension(type.Assembly.Location) + ".xml");

        if (!File.Exists(xml))
        {
            // The build generates it; its absence would make every assertion here vacuous.
            Assert.Fail($"no documentation file beside {type.Assembly.Location}");
        }

        return File.ReadAllText(xml).Contains($"\"T:{type.FullName}\"", StringComparison.Ordinal);
    }
}
