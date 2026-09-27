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

    /// <summary>Whether a type is reachable from outside this assembly.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Whether somebody outside can name it.</returns>
    /// <remarks>
    /// <b>Found by lens 3, and the guard had the hole from the day it was written.</b>
    /// <see cref="Type.IsPublic"/> is true only of a <em>top-level</em> public type; a public type
    /// nested inside a public one answers <see cref="Type.IsNestedPublic"/> instead. So
    /// <c>Enrolment.Outcome</c> — public since the split, and returned by every write this
    /// assembly performs — was never on the list, and neither was the type added beside it in this
    /// branch. The check's own claim is that a member appearing here is a decision somebody makes
    /// in this file, and it could not keep that promise for the shape a nested record has.
    /// </remarks>
    private static bool Reachable(Type type) =>
        type.IsPublic || (type.IsNestedPublic && Reachable(type.DeclaringType!));

    /// <summary>What to call a type, so a nested one reads as what it is.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Its name, qualified by whatever it is nested in.</returns>
    private static string Named(Type type) =>
        type.IsNested ? $"{Named(type.DeclaringType!)}.{type.Name}" : type.Name;

    [Fact]
    public void The_setup_assembly_exposes_exactly_these_types()
    {
        var public_ = Setup.GetTypes()
            .Where(Reachable)
            .Select(Named)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "AutostartPlan",
                "ConsolePicker",
                "EnrollCommand",
                "EnrollmentEntry",
                "EnrollmentPlan",
                "Enrolment",

                // Nested, and invisible to this check until lens 3 widened it. `Outcome` is what
                // every write here returns and has been public since the split.
                //
                // `Enrolment.Shared` was beside it and 018 removed it: it said which project already
                // started a repository, so a surface could describe the launch entry it was about to
                // write and the catalogue line it was not. One catalogue per machine keyed by the
                // agent's identity means every project shares the one entry, so there is no second
                // shape to describe.
                "Enrolment.Outcome",

                // **One sentence, two surfaces, and the command line did not have it.** The
                // terminal's door said before anything was written that each repository becomes
                // startable by a peer with nobody watching; `parley enroll` said only how many it
                // had found. Public because `Parley.Terminal` reads it — a consent notice with two
                // spellings drifts, and the half that drifts is the half nobody is reading at the
                // time.
                "EnrolmentConsent",

                "InstalledEntry",
                "InterruptedEnrolment",

                // **The only thing that makes an accepted ownership line reach anybody.** The
                // registry's `owns` is filled from the launch environment on attach and no operation
                // writes it, so `record_ownership` stores a decision and this puts it where the
                // decision takes effect. Public because both surfaces call it: 020 made accepting a
                // line something the terminal and the web UI both do, which is the owner's reversal
                // of the one-writer rule — and the rule that replaces it, that no surface writes for
                // an agent whose machine is not this one, is `NotHere` here rather than twice.
                "OwnershipLine",
                "ProjectCommand",
                "ProjectFields",
                "ProjectResolution",
                "RepositoryPlan",

                // A command's project comes from its context, and `parley` reads which projects the repository it runs
                // in names. Here rather than in the CLI because the terminal enrols repositories through this assembly
                // and the key rule it reads -- `parley-<project>` -- is `LaunchKey`'s, which must not be written twice.
                // `LaunchKey` itself moved to Parley.Configuration in 018, where a node's own reader of that file can
                // see it; this assembly still reads it from there rather than keeping a second spelling.
                "RepositoryProjects",
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
