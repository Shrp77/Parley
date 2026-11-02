using System.Text.Json;
using Parley.Configuration;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a JSON file holding a name or string that is not valid text is refused as unreadable, with a
/// <see cref="JsonException"/> that names no key, rather than an exception no caller catches.
/// </summary>
/// <remarks>
/// Lens 1 run 132: refusing duplicates makes the runtime decode every member name at the parse, and a lone surrogate
/// escape, which JavaScript's <c>JSON.stringify</c> writes for a string it cut in half, threw
/// <see cref="InvalidOperationException"/> out of it, even in another server's <c>env</c> that nobody reads. An agent
/// could not attach and every <c>parley</c> command in the repository crashed.
/// </remarks>
[Unit]
public sealed class BrokenTextIsRefusedTests
{
    // A lone high surrogate in a top-level name.
    private const string TopLevelName = """
        { "mcpServers": {}, "T\uD800": 1 }
        """;

    // A lone surrogate in a name inside another server's env, which nothing in Parley reads.
    private const string NestedName = """
        { "mcpServers": { "other": { "command": "node", "env": { "K\uD800": "v" } },
          "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } } } }
        """;

    // A lone low surrogate, the other half on its own.
    private const string LowHalfName = """
        { "x": { "y": { "T\uDC00": 1 } } }
        """;

    // A lone surrogate in a value, which the runtime decodes only when somebody reads it.
    private const string BrokenValue = """
        { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders\uD800" } } } }
        """;

    // The broken name written twice, and a broken name beside an unrelated duplicate.
    private const string BrokenNameTwice = """
        { "x": { "T\uD800": 1, "T\uD800": 2 } }
        """;

    private const string BrokenNameBesideADuplicate = """
        { "a": 1, "a": 2, "x": { "T\uD800": 1 } }
        """;

    public static TheoryData<string> Broken => [TopLevelName, NestedName, LowHalfName, BrokenValue];

    public static TheoryData<string> BrokenAndDuplicated => [BrokenNameTwice, BrokenNameBesideADuplicate];

    [Theory]
    [MemberData(nameof(Broken))]
    public void A_parsed_node_refuses_it_in_fixed_words(string text)
    {
        var refused = Assert.Throws<JsonException>(() => StrictJson.Parse(text));

        Assert.Equal(StrictJson.BrokenTextMessage, refused.Message);
    }

    [Theory]
    [MemberData(nameof(Broken))]
    public void A_parsed_document_refuses_it_in_fixed_words(string text)
    {
        var refused = Assert.Throws<JsonException>(() => StrictJson.Document(text).Dispose());

        Assert.Equal(StrictJson.BrokenTextMessage, refused.Message);
    }

    [Theory]
    [MemberData(nameof(BrokenAndDuplicated))]
    public void With_a_duplicate_too_it_is_still_refused_in_fixed_words(string text)
    {
        // Which fault the runtime meets first is its business; either way the caller sees an ordinary refusal.
        string[] fixedWords = [StrictJson.BrokenTextMessage, StrictJson.DuplicateMessage];

        Assert.Contains(Assert.Throws<JsonException>(() => StrictJson.Parse(text)).Message, fixedWords);
        Assert.Contains(Assert.Throws<JsonException>(() => StrictJson.Document(text).Dispose()).Message, fixedWords);
    }

    [Fact]
    public void A_lone_surrogate_character_in_the_text_itself_is_refused_too()
    {
        var text = "{ \"a\": \"x" + (char)0xD800 + "\" }";

        Assert.Equal(StrictJson.BrokenTextMessage, Assert.Throws<JsonException>(() => StrictJson.Parse(text)).Message);
        Assert.Equal(
            StrictJson.BrokenTextMessage,
            Assert.Throws<JsonException>(() => StrictJson.Document(text).Dispose()).Message);
    }

    [Fact]
    public void A_whole_surrogate_pair_is_ordinary_text()
    {
        var node = StrictJson.Parse("""{ "smile\uD83D\uDE00": "and\uD83D\uDE00" }""");

        Assert.Equal("and" + char.ConvertFromUtf32(0x1F600), (string?)node?["smile" + char.ConvertFromUtf32(0x1F600)]);
    }

    [Fact]
    public void The_identity_in_such_a_file_is_none_rather_than_a_throw()
    {
        Assert.Equal(default, EnrollmentPlan.IdentityIn(NestedName));
    }
}

/// <summary>That a repository whose launch file holds text that is not valid reads as unreadable, not as a throw.</summary>
/// <remarks>The file half of <see cref="BrokenTextIsRefusedTests"/>, which parses the same texts in memory.</remarks>
[Integration]
public sealed class ALaunchFileHoldingBrokenTextIsUnreadableTests
{
    [Theory]
    [MemberData(nameof(BrokenTextIsRefusedTests.Broken), MemberType = typeof(BrokenTextIsRefusedTests))]
    public void A_repository_whose_launch_file_holds_it_reads_as_unreadable(string text)
    {
        var workspace = Directory.CreateTempSubdirectory("parley-broken-text-");

        try
        {
            File.WriteAllText(Path.Combine(workspace.FullName, LaunchConfiguration.FileName), text);

            Assert.False(LaunchConfiguration.TryRead(workspace.FullName, out var configuration, out var problem));
            Assert.Same(LaunchConfiguration.None, configuration);
            Assert.Equal(StrictJson.BrokenTextMessage, problem);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
}
