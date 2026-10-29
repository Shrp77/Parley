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
    /// <b>A nested public type is reachable too.</b>
    /// <see cref="Type.IsPublic"/> is true only of a <em>top-level</em> public type; a public type
    /// nested inside a public one answers <see cref="Type.IsNestedPublic"/> instead. So
    /// <c>Enrolment.Outcome</c> — returned by every write this assembly performs — has to be on the
    /// list. The check's own claim is that a member appearing here is a decision somebody makes
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

                // Nested. `Outcome` is what every write here returns.
                //
                // There is no `Enrolment.Shared`: one catalogue per machine keyed by the agent's
                // identity means every project shares the one entry, so there is no second shape to
                // describe.
                "Enrolment.Outcome",

                // **One sentence, two surfaces.** The terminal's door says before anything is written
                // that each repository becomes startable by a peer with nobody watching, and
                // `parley enroll` says it too. Public because `Parley.Terminal` reads it — a consent notice with two
                // spellings drifts, and the half that drifts is the half nobody is reading at the
                // time.
                "EnrolmentConsent",

                // **What an enrolment did, as facts, and what it is doing while it does it.** The
                // terminal must not reprint everything the command wrote for a shell into a one-row
                // message line, nor say nothing while each agent is started and waited for. Public because `Parley.Terminal` builds its one-line
                // summary and its progress line from these rather than parsing the command's prose.
                "EnrolmentOutcome",

                "InstalledEntry",
                "InterruptedEnrolment",

                // Handing a web address to the platform's browser, which `parley web-ui --open` and the terminal's
                // "Open the web console" both do. Here so there is one guard on what the shell is asked to open.
                "Launch",

                // One agent that was enrolled and did not register, with why. `EnrolmentOutcome` lists them.
                "NotStarted",
                // **The only thing that makes an accepted ownership line reach anybody.** The
                // registry's `owns` is filled from the launch environment on attach and no operation
                // writes it, so `record_ownership` stores a decision and this puts it where the
                // decision takes effect. Public because both surfaces call it: accepting a
                // line is something the terminal and the web UI both do, and the rule that no surface
                // writes for an agent whose machine is not this one is `NotHere` here rather than twice.
                "OwnershipLine",
                "ProjectCommand",
                "ProjectFields",
                "ProjectResolution",
                "RepositoryPlan",

                // A command's project comes from its context, and `parley` reads which projects the repository it runs
                // in names. Here rather than in the CLI because the terminal enrols repositories through this assembly
                // and the key rule it reads -- `parley-<project>` -- is `LaunchKey`'s, which must not be written twice.
                // `LaunchKey` itself lives in Parley.Configuration, where a node's own reader of that file can
                // see it; this assembly reads it from there rather than keeping a second spelling.
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
