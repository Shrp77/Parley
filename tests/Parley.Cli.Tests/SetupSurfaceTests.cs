using System.Reflection;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What extracting the machine-setup half of the CLI made public, and what it must not have.
/// </summary>
/// <remarks>
/// <para>
/// Splitting an assembly widens things. `Enrolment` and `ScheduledTasks` were internal and had to
/// become public to cross the new boundary, and `internal` is where a type says "nobody outside has
/// to live with this" — so the move turned two implementation details into a surface two tools now
/// depend on, in a change whose whole claim was that nothing changed.
/// </para>
/// <para>
/// Written out rather than counted, so a member added here is a decision somebody makes in this
/// file rather than a number that quietly moves.
/// </para>
/// </remarks>
public sealed class SetupSurfaceTests
{
    private static readonly Assembly Setup = typeof(Enrolment).Assembly;

    [Fact]
    public void The_setup_assembly_exposes_exactly_these_types()
    {
        var public_ = Setup.GetTypes()
            .Where(type => type.IsPublic)
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "AutostartPlan",
                "ConsolePicker",
                "EnrollCommand",
                "EnrollmentEntry",
                "EnrollmentManifest",
                "EnrollmentPlan",
                "Enrolment",
                "InstalledEntry",
                "LaunchEntries",
                "LaunchEntry",
                "LaunchKey",
                "ProjectCommand",
                "ProjectFields",
                "ProjectResolution",
                "RepositoryPlan",
                "RepositorySelection",
                "Resolved",
                "ScheduledTasks",
                "SelectableRepository",
                "SelectionStatus",
                "Supervisor",
                "ValueSource",
            ],
            public_);
    }

    [Fact]
    public void Nothing_here_can_open_a_store_or_serve_a_page()
    {
        // The boundary this project exists inside. GraphTests walks the project references; this
        // asks the loaded assembly, which is the other half of the same question — a reference
        // acquired through a package rather than a project would pass that walk and fail here.
        var referenced = Setup.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToList();

        foreach (var forbidden in new[] { "Parley.Store", "Parley.Broker", "Parley.Headless",
                                          "Parley.Web", "Microsoft.Data.Sqlite", "Dapper" })
        {
            Assert.DoesNotContain(
                referenced,
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        // Non-vacuity: it does reference the two it is supposed to.
        Assert.Contains(referenced, name => name == "Parley.Configuration");
        Assert.Contains(referenced, name => name == "Parley.Core");
    }

    [Fact]
    public void The_two_types_the_move_widened_are_the_only_two_it_widened()
    {
        // `Enrolment` and `ScheduledTasks` crossed from internal to public because the terminal is
        // in another assembly now. Anything else that became public in the same change would have
        // been widened for no reason at all, and nothing would have said so.
        //
        // Read from the source rather than from reflection, because what is being checked is that
        // these two carry the note explaining why they are public and no third one does.
        var widened = new[] { "Enrolment", "ScheduledTasks" };

        foreach (var name in widened)
        {
            var type = Setup.GetTypes().Single(t => t.Name == name);

            Assert.True(type.IsPublic, $"{name} was widened for the move and has gone back.");
            Assert.True(type.IsAbstract && type.IsSealed, $"{name} is not static any more.");
        }
    }

    [Fact]
    public void No_public_member_here_hands_out_a_credential()
    {
        // CLAUDE.md's standing constraint: credentials are references, never values. This assembly
        // writes launch configurations, which is where a credential reference would be written --
        // so a member that returned one resolved would be the breach, and there is no such member.
        var resolving = Setup.GetTypes()
            .Where(type => type.IsPublic)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static
                                                | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName)
            .Where(method =>
                method.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .ToList();

        Assert.True(
            resolving.Count == 0,
            "these look like they resolve a credential: " + string.Join(", ", resolving));
    }
}
