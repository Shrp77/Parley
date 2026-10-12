using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That an agent is told who the person is and that waiting for them is correct (SPEC.md principle 2.6, §8 rule 10,
/// FR-013).
/// </summary>
/// <remarks>
/// <para>
/// <b>Instruction text is the only way an agent learns this.</b> Both texts once said "if nobody is seated,
/// report blocked" — and since a seat was released after twenty idle minutes, "nobody is seated" was true every time
/// the person stepped away, so an agent waiting on their decision was told to give up on it. The broker now always
/// provides the person; the words have to stop contradicting it.
/// </para>
/// <para>
/// <b>Both places, because an agent reads both.</b> The standing instructions arrive at initialize; the
/// <c>parley_result</c> description is what it reads at the moment it is deciding whether to report blocked, which
/// is exactly when the old sentence did its harm. Watched to fail against 0.35.x.
/// </para>
/// </remarks>
public sealed class PersonInstructionsTests
{
    private static string ResultDescription() =>
        typeof(ParleyTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "parley_result")
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;

    public static TheoryData<string> Texts() => new() { "instructions", "parley_result" };

    private static string Text(string which) => which == "instructions" ? ParleyInstructions.Text : ResultDescription();

    [Theory]
    [MemberData(nameof(Texts))]
    public void It_names_the_person_by_the_role_an_agent_can_find_in_its_roster(string which) =>
        Assert.Contains("human-user", Text(which), StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(Texts))]
    public void It_says_waiting_for_the_person_is_correct(string which)
    {
        var text = Text(which);

        Assert.Contains("away", text, StringComparison.Ordinal);
        Assert.Contains("waiting for their decision is correct", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void It_no_longer_says_a_missing_person_is_a_reason_to_give_up(string which)
    {
        var text = Text(which);

        Assert.DoesNotContain("nobody is seated", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("person seated", text, StringComparison.OrdinalIgnoreCase);
    }
}
