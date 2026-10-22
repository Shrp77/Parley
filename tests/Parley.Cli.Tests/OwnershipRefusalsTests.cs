using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Every refusal this feature owes, and the four things it must never say.
/// </summary>
/// <remarks>
/// <para>
/// <b>`contracts/refusals.md` is the list, and the rule it opens with is the expensive one:</b> a
/// refusal names what replaced it or what to do instead. Tests are pinned to exactly
/// that, and the reason is that removing a thing is cheap while replacing one means finding everything
/// that ever recommended it.
/// </para>
/// <para>
/// <b>The last section is the one nothing else guards.</b> Four things must never be said, each because
/// saying it was a defect somewhere else: the word <c>reachable</c> about a port, the integer that
/// spells <em>no cap</em>, <c>seen</c> for an answer, and a count of characters a person wrote
/// attributed to the agent. None of those is a wording preference — each is a sentence that would tell
/// somebody a fact that is not true.
/// </para>
/// </remarks>
public sealed class OwnershipRefusalsTests
{
    private static AgentName Elsewhere(string machine = "server-b") =>
        AgentName.Create($"orders@{machine}");

    [Fact]
    public void An_empty_statement_is_refused_and_declining_is_offered_instead()
    {
        // Declining is the honest route to "no line", so the refusal has to name it. Must fail against a
        // refusal that only says no: somebody who wants the agent to have no line would otherwise be
        // left typing spaces at it.
        var wrong = new OwnershipAcceptance("   ", OwnershipSource.Composed, null, null).Problem();

        Assert.NotNull(wrong);
        Assert.Contains("not a statement of ownership", wrong, StringComparison.Ordinal);
        Assert.Contains("Decline instead", wrong, StringComparison.Ordinal);
    }

    [Fact]
    public void A_character_that_could_forge_a_roster_entry_is_refused_and_located()
    {
        // A statement has no length bound, so "it contains U+2028" is not actionable advice
        // about a paragraph. Must fail against a refusal that names no position.
        Assert.False(
            OwnershipStatement.IsValid("Orders and" + (char)0x2028 + "invoicing.", out var wrong));

        Assert.NotNull(wrong);
        Assert.Contains("U+2028", wrong, StringComparison.Ordinal);
        Assert.Contains("position 11", wrong, StringComparison.Ordinal);
        Assert.Contains("every agent reads", wrong, StringComparison.Ordinal);
    }

    [Fact]
    public void A_statement_is_refused_rather_than_quietly_trimmed()
    {
        // **Refused where the operator sees it and can act.** Silently removing the separator would
        // admit the intent and repair the symptom, and the person would never learn the field is one
        // line. Must fail against any implementation that returns true having cleaned the input.
        //
        // **Built with `(char)` rather than written as escapes, and that is not a style choice.** An
        // escape for one of these written into this file is unescaped by the tooling that writes it and
        // lands as the raw character, which breaks the build with `CS1010: Newline in constant` -- a
        // failure that looks like nothing is wrong with the line you are reading. CLAUDE.md records it;
        // this file walked into it anyway, and a first draft of *this comment* then carried a raw
        // U+202E, which reverses the display of everything after it. None of them is written here.
        int[] sneaky =
        [
            0x2028, // LINE SEPARATOR: a line break wherever this is rendered
            0x2029, // PARAGRAPH SEPARATOR: the same
            0x202E, // RIGHT-TO-LEFT OVERRIDE: reverses the display of everything after it
            0x200B, // ZERO WIDTH SPACE: invisible
            0xFEFF, // ZERO WIDTH NO-BREAK SPACE: invisible
        ];

        foreach (var point in sneaky)
        {
            var statement = "Orders" + (char)point + "and invoicing.";

            Assert.False(
                OwnershipStatement.IsValid(statement, out var wrong),
                $"U+{point:X4} was admitted");

            // And the refusal says which one and where, rather than that something was wrong.
            Assert.Contains($"U+{point:X4}", wrong!, StringComparison.Ordinal);
            Assert.Contains("position 7", wrong!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_agent_on_another_machine_is_refused_and_told_where_to_go()
    {
        // research.md R3, and the rule is the same for both surfaces. Must fail against a refusal that
        // says only "cannot write": the person is holding a machine that genuinely cannot do this, and
        // what they need is the name of the one that can.
        var wrong = OwnershipLine.NotHere(Elsewhere());

        Assert.NotNull(wrong);
        Assert.Contains("server-b", wrong, StringComparison.Ordinal);
        Assert.Contains("a file there", wrong, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_on_this_machine_is_not_refused()
    {
        // The other side, and it is what stops the guard above from being a guard that refuses
        // everything. Built from the machine this suite fixes rather than from a literal.
        Assert.Null(OwnershipLine.NotHere(AgentName.Create($"orders@{MachineName.Current}")));
    }

    [Fact]
    public void A_proposal_that_names_no_conversation_is_refused()
    {
        // An accepted line must be traceable to the discussion that produced it, so a record
        // claiming the agent proposed it and naming nowhere is not traceable. Must fail against
        // accepting it and leaving the column null.
        var wrong = new OwnershipAcceptance("Orders.", OwnershipSource.Proposed, 7, null).Problem();

        Assert.NotNull(wrong);
        Assert.Contains("the conversation must be named", wrong, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composed_line_may_not_claim_a_proposal_or_a_discussion()
    {
        // The inverse, and it is what keeps the evidence honest: a line a person typed has no agent's
        // proposal behind it, so a length or a conversation recorded against it would be an invention.
        Assert.NotNull(
            new OwnershipAcceptance("Orders.", OwnershipSource.Composed, 7, null).Problem());

        Assert.NotNull(
            new OwnershipAcceptance("Orders.", OwnershipSource.Composed, null, "owns-orders-1").Problem());
    }

    [Fact]
    public void Nothing_names_the_integer_that_spells_no_cap()
    {
        // **The precedent.** `parley clarifications` says "Caps in force: none" rather than the
        // number, because telling somebody a bound was set at 2,147,483,647 tells them a value nobody
        // chose. `OwnershipStatement.Bounded` exists so nothing has to print it.
        //
        // Must fail against a refusal, a field description or a help string that names the sentinel.
        Assert.False(OwnershipStatement.Bounded);

        var long_enough = new string('x', 500);

        Assert.True(OwnershipStatement.IsValid(long_enough, out var wrong));
        Assert.Null(wrong);

        foreach (var source in Sources())
        {
            Assert.DoesNotContain(
                "2147483647",
                File.ReadAllText(source),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_says_seen_about_an_answer_or_reachable_about_a_broker()
    {
        // Two words, two defects. **`seen`** claims to know what a model did with a message,
        // where the broker only knows it handed it over -- which is why every surface
        // says *collected*. **`reachable`** collapses two facts that are kept apart: whether an
        // agent answered, and who may address it.
        //
        // Read from the sources that carry this feature's words, because the rule is about what a
        // person is told and there is no other way to check a sentence nobody has written yet.
        foreach (var source in Sources())
        {
            var text = File.ReadAllText(source);

            Assert.DoesNotContain(" seen ", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reachable port", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The files carrying this feature's refusals and reports.</summary>
    /// <returns>Their full paths.</returns>
    /// <remarks>
    /// Named rather than globbed, so adding a surface is a decision somebody makes in this list — the
    /// same reason <c>SetupSurfaceTests</c> keeps its types by hand. A glob would quietly cover new
    /// files and quietly stop covering renamed ones.
    /// </remarks>
    private static IEnumerable<string> Sources()
    {
        string[] relative =
        [
            Path.Combine("src", "Parley.Core", "OwnershipStatement.cs"),
            Path.Combine("src", "Parley.Core", "OwnershipAcceptance.cs"),
            Path.Combine("src", "Parley.Setup", "OwnershipLine.cs"),
            Path.Combine("src", "Parley.Client", "OwnershipAsk.cs"),
            Path.Combine("src", "Parley.Client", "OwnershipProposal.cs"),
            Path.Combine("src", "Parley.Terminal", "Ownership.cs"),

            // The facilitator's words, the review's words and the switch's, on both surfaces.
            Path.Combine("src", "Parley.Core", "Facilitation.cs"),
            Path.Combine("src", "Parley.Broker", "Facilitator.cs"),
            Path.Combine("src", "Parley.Client", "Provenance.cs"),
            Path.Combine("src", "Parley.Client", "Rendering", "Speaker.cs"),
            Path.Combine("src", "Parley.Terminal", "OwnershipReview.cs"),
            Path.Combine("src", "Parley.Terminal", "AutoAccepting.cs"),
            Path.Combine("src", "Parley.Web", "Pages", "Projects", "Ownership.cshtml"),
            Path.Combine("src", "Parley.Web", "Pages", "Projects", "Ownership.cshtml.cs"),
        ];

        var root = new DirectoryInfo(AppContext.BaseDirectory);

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Parley.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        foreach (var path in relative)
        {
            var full = Path.Combine(root.FullName, path);

            Assert.True(File.Exists(full), $"{path} is not there; this list has moved");

            yield return full;
        }
    }
}
