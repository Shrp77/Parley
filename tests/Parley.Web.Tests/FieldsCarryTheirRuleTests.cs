using Parley.Client;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console's two constrained fields carry their rule into the page.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same defect the terminal had, in the same week.</b> The owner named a conversation
/// <c>tell everybody test</c> in <c>parley top</c> and was refused after the form had closed; this
/// console's <i>Start a conversation called</i> field had no check at all and a placeholder —
/// "what it is about" — that invited a sentence into a field that cannot hold one.
/// </para>
/// <para>
/// <b>Three layers, and this checks the two the server owns.</b> <c>pattern</c> and
/// <c>maxlength</c> are what a browser with no scripts enforces; <c>data-correct</c> is what the
/// frame's own script applies as somebody types; and the server refuses whatever reaches it
/// regardless. Every one of them is spelled from the same constant, so a rule cannot drift into
/// three versions of itself.
/// </para>
/// </remarks>
public sealed class FieldsCarryTheirRuleTests
{
    [Fact]
    public async Task Starting_a_conversation_says_what_may_name_one()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(
            new Uri($"/Projects/Agents?project={DeadBrokerConsole.Project}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("id=\"new-conversation\"", body, StringComparison.Ordinal);

        // The rule, as a browser enforces it and as the field applies it.
        Assert.Contains("maxlength=\"64\"", body, StringComparison.Ordinal);
        Assert.Contains(ConversationName.Pattern, body, StringComparison.Ordinal);
        Assert.Contains(
            $"data-correct=\"{ConversationName.Allowed}\"", body, StringComparison.Ordinal);

        // And in words, because a pattern attribute is not something a person reads. Decoded
        // first: the rule names `'.'` and `'_'`, and Razor encodes an apostrophe.
        Assert.Contains(
            ConversationName.Rule,
            System.Net.WebUtility.HtmlDecode(body),
            StringComparison.Ordinal);

        // The placeholder no longer invites prose into it.
        Assert.DoesNotContain("placeholder=\"what it is about\"", body, StringComparison.Ordinal);
    }

    // `Joining_says_what_may_name_a_person` was here. The Join page and its name field are gone: the broker
    // provides the person seat, so no name is typed.

    [Fact]
    public async Task The_frame_carries_one_implementation_of_the_correction()
    {
        // On every page, because both fields are on different ones and a script copied into two
        // pages is two scripts the first time either is edited.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(
            new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        // Read from the projects page, as there is no Join page: the script is the frame's,
        // so every page carries it.
        Assert.Contains("querySelectorAll('[data-correct]')", body, StringComparison.Ordinal);

        // A space becomes the separator the rule allows rather than being dropped, which is the
        // owner's own choice and the one thing the character class cannot express. Walked
        // character by character rather than by collapsing the whitespace first: the two give
        // different answers for `1.2 / notes`, and a console that named it differently from the
        // terminal is what one shared rule exists to prevent.
        Assert.Contains("wasSpace", body, StringComparison.Ordinal);
        Assert.DoesNotContain("replace(/\\s+/g", body, StringComparison.Ordinal);
    }
}
