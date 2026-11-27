using System.Text.Json;
using Parley.Configuration;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a JSON object naming one member twice is refused when it is parsed, in words that carry no key.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 run 137: <c>JsonNode.Parse</c> accepted a repeated member and the <c>ArgumentException</c> came later,
/// when the object was indexed, out of call sites that catch only the ordinary "unreadable" exceptions. .NET then
/// printed the raw key, escape sequences and all, and one repository's <c>.mcp.json</c> ended a scan of every
/// sibling.
/// </para>
/// <para>
/// The keys here hold an escape sequence that would clear the screen and retitle the window, so a message that
/// quoted the key would carry it.
/// </para>
/// </remarks>
[Unit]
public sealed class DuplicatedMembersAreRefusedTests
{
    private const char Escape = (char)0x1B;

    // A top-level key written twice, as a careless merge leaves it, with an escape sequence in the key.
    private const string TopLevelTwice = """
        { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } } },
          "x\u001b[2J\u001b]0;owned\u0007": 1,
          "x\u001b[2J\u001b]0;owned\u0007": 2 }
        """;

    // A key written twice inside one entry's env, which no server-name check sees.
    private const string EnvKeyTwice = """
        { "mcpServers": { "parley-alpha": { "command": "parley-mcp",
          "env": { "PARLEY_AGENT": "orders", "K\u001b[2J": "a", "K\u001b[2J": "b" } } } }
        """;

    private static EnrollmentEntry Entry() => new("/repos/orders", "orders", "orders, nothing else", Unattended: false);

    [Theory]
    [InlineData(TopLevelTwice)]
    [InlineData(EnvKeyTwice)]
    public void A_parsed_node_refuses_the_duplicate_with_no_key_in_the_message(string text)
    {
        var refused = Assert.Throws<JsonException>(() => StrictJson.Parse(text));

        Assert.Equal(StrictJson.DuplicateMessage, refused.Message);
        Assert.DoesNotContain(Escape, refused.Message);
    }

    [Theory]
    [InlineData(TopLevelTwice)]
    [InlineData(EnvKeyTwice)]
    public void A_parsed_document_refuses_the_duplicate_with_no_key_in_the_message(string text)
    {
        var refused = Assert.Throws<JsonException>(() => StrictJson.Document(text).Dispose());

        Assert.Equal(StrictJson.DuplicateMessage, refused.Message);
        Assert.DoesNotContain(Escape, refused.Message);
    }

    [Fact]
    public void The_other_reading_options_still_apply()
    {
        // RepositoryProjects reads with trailing commas and comments allowed; refusing duplicates must not drop them.
        var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

        Assert.NotNull(StrictJson.Parse("""{ "a": 1, /* note */ "b": 2, }""", options));
        Assert.Throws<JsonException>(() => StrictJson.Parse("""{ "a": 1, "a": 2, }""", options));
    }

    [Fact]
    public void Text_that_is_not_json_keeps_the_runtime_message()
    {
        // Only the duplicate's message is replaced; the others say where the fault is, which is worth keeping.
        var refused = Assert.ThrowsAny<JsonException>(() => StrictJson.Parse("{ \"a\": "));

        Assert.NotEqual(StrictJson.DuplicateMessage, refused.Message);
    }

    [Theory]
    [InlineData(TopLevelTwice)]
    [InlineData(EnvKeyTwice)]
    public void The_identity_in_a_file_with_a_duplicate_is_none_rather_than_a_throw(string text)
    {
        Assert.Equal(default, EnrollmentPlan.IdentityIn(text));
    }

    [Theory]
    [InlineData(TopLevelTwice)]
    [InlineData(EnvKeyTwice)]
    public void Describing_an_enrolment_into_a_file_with_a_duplicate_is_the_ordinary_refusal(string text)
    {
        // JsonException is what the enrol loop catches and reports as "cannot be merged into"; an ArgumentException
        // escaped it and ended the run.
        var refused = Assert.Throws<JsonException>(
            () => EnrollmentPlan.Describe(Entry(), text, null, false, project: "alpha"));

        Assert.DoesNotContain(Escape, refused.Message);
    }
}
