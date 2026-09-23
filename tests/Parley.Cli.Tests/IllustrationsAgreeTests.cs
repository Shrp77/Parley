using Parley.Configuration;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the four illustrations of a launch configuration agree with each other and with the writer.
/// </summary>
/// <remarks>
/// <para>
/// Review gate, lens 3, run 8. FR-014 requires every illustration a person is shown to carry the
/// project-keyed shape, and T043 updated them by hand — a task which named three of the four, the
/// fourth being the adapter's own header comment. Nothing checked that they agree, and a rule
/// satisfied by hand across four files is a rule that holds until somebody edits one.
/// </para>
/// <para>
/// <b>The key is the easy half and was already right everywhere.</b> What differed is the variable
/// that decides which deployment the agent joins: the console's registration page showed
/// <c>PARLEY_BROKER</c> and nothing else, which is the address-pinned shape this feature's refusals
/// are written against — so the console instructed a person to hand-write the one entry the tooling
/// later declines to re-point.
/// </para>
/// </remarks>
public sealed class IllustrationsAgreeTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>The four, named by FR-014 and by the task that missed one of them.</summary>
    public static TheoryData<string> Illustrations =>
    [
        "README.md",
        Path.Combine("plugins", "parley", "commands", "setup.md"),
        Path.Combine("src", "Parley.Web", "Pages", "Projects", "Register.cshtml"),
        Path.Combine("src", "Parley.Mcp", "Program.cs"),
    ];

    private static string Read(string relative)
    {
        var path = Path.Combine(Root, relative);

        Assert.True(File.Exists(path), $"{relative} is not there; FR-014's list has moved");

        return File.ReadAllText(path).ReplaceLineEndings("\n");
    }

    [Theory]
    [MemberData(nameof(Illustrations))]
    public void Each_shows_the_project_keyed_shape(string relative)
    {
        var text = Read(relative);

        Assert.Contains(LaunchKey.Prefix, text, StringComparison.Ordinal);
        Assert.Contains(EnrollmentPlan.AdapterCommand, text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Illustrations))]
    public void Each_names_the_project_rather_than_only_an_address(string relative)
    {
        // **The half that decides which deployment an agent joins.** An entry carrying only
        // `PARLEY_BROKER` is what this tooling treats as pinned to an address: enrolling that
        // repository later writes the project's entry *beside* it rather than through it, and two of
        // FR-008's three outcomes are refusals. An illustration may mention the address form — there
        // is a real case for it, a machine whose manifest does not name the project — but it may not
        // be the only thing it shows, or it teaches the shape the rest of the system works around.
        var text = Read(relative);

        Assert.Contains(EnrollmentPlan.ProjectVariable, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_writer_still_writes_what_they_illustrate()
    {
        // The control, and the thing that makes the two above more than a spell-check: the variables
        // are asserted against what `Describe` actually emits, so an illustration cannot agree with
        // three others and all four be wrong together.
        var plan = EnrollmentPlan.Describe(
            new EnrollmentEntry("/src/orders", "orders", "Carts and totals.", Unattended: false)
            {
                Enroll = true,
                OwnsApproved = true,
            },
            null,
            null,
            false,
            "acme");

        Assert.Contains(EnrollmentPlan.ProjectVariable, plan.LaunchConfiguration!, StringComparison.Ordinal);
        Assert.Contains(LaunchKey.For("acme"), plan.LaunchConfiguration!, StringComparison.Ordinal);
        Assert.DoesNotContain(EnrollmentPlan.BrokerVariable, plan.LaunchConfiguration!, StringComparison.Ordinal);
    }
}
