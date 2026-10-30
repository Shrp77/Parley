using System.Reflection;
using Xunit;
using Xunit.v3;

namespace Parley.Testing;

/// <summary>Marks a test class as a unit test: one module, in memory, with nothing outside the process.</summary>
/// <remarks>
/// <para>
/// <b>The bar is strict, and that is the point.</b> A unit test touches no file, no database (an in-memory SQLite database
/// included), no socket, no process, no clock it does not own and no UI, and exercises one module. Anything else is
/// <see cref="IntegrationAttribute"/>, even a test of two modules together.
/// </para>
/// <para>
/// Unit tests run on every push; integration tests run nightly and before a pull request merges
/// (<c>.github/workflows/nightly.yml</c>). The category is a trait, so a runner selects it with
/// <c>-trait "Category=Unit"</c>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class UnitAttribute : Attribute, ITraitAttribute
{
    /// <summary>The trait's value.</summary>
    public const string Value = "Unit";

    /// <inheritdoc/>
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new(TestCategory.Trait, Value)];
}

/// <summary>Marks a test class as an integration test: anything that is not a pure <see cref="UnitAttribute"/> test.</summary>
/// <remarks>
/// Files, databases (in memory included), sockets, processes, a driven UI, or two modules working together.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class IntegrationAttribute : Attribute, ITraitAttribute
{
    /// <summary>The trait's value.</summary>
    public const string Value = "Integration";

    /// <inheritdoc/>
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new(TestCategory.Trait, Value)];
}

/// <summary>The trait a category is recorded under.</summary>
public static class TestCategory
{
    /// <summary>The trait's name.</summary>
    public const string Trait = "Category";
}

/// <summary>That every test class in this assembly says whether it is a unit or an integration test.</summary>
/// <remarks>
/// A class with neither would run in neither selection's place: left out of the push run if it is a unit test, and
/// slowing it down if it is not. Linked into every test project, so each holds its own assembly to the rule.
/// </remarks>
[Unit]
public sealed class EveryTestClassHasACategoryTests
{
    [Fact]
    public void Every_class_with_a_test_is_marked_unit_or_integration_and_not_both()
    {
        var wrong = typeof(EveryTestClassHasACategoryTests).Assembly.GetTypes()
            .Where(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Any(method => method.IsDefined(typeof(FactAttribute), inherit: true)))
            .Where(type => type.IsDefined(typeof(UnitAttribute), inherit: true) == type.IsDefined(typeof(IntegrationAttribute), inherit: true))
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(wrong.Count == 0, "Mark each with [Unit] or [Integration] (exactly one): " + string.Join(", ", wrong));
    }
}
