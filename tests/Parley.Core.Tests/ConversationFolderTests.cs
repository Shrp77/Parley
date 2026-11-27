using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// Which folder a conversation's files go in (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// Each case is a collision that was found rather than imagined: a reserved device name, a name Windows strips
/// the dot from, a name Linux hides, and two names one folder on Windows and macOS. The first design mapped a
/// reserved name to <c>_con</c>, which a conversation may legitimately be called.
/// </remarks>
[Unit]
public sealed class ConversationFolderTests
{
    private static Func<string, bool> Taken(params string[] folders) =>
        candidate => folders.Contains(candidate, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void An_ordinary_identifier_is_its_own_folder()
    {
        Assert.Equal("tax-inclusive-pricing", ConversationFolder.Choose("tax-inclusive-pricing", Taken()));
    }

    [Theory]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("com1.notes")]
    [InlineData("pricing.")]
    [InlineData(".pricing")]
    public void An_identifier_that_cannot_be_a_folder_as_it_is_gets_a_suffix(string identifier)
    {
        Assert.Equal(identifier + "~2", ConversationFolder.Choose(identifier, Taken()));
    }

    [Fact]
    public void An_identifier_differing_only_in_case_from_a_taken_folder_gets_a_suffix()
    {
        Assert.Equal("Pricing~2", ConversationFolder.Choose("Pricing", Taken("pricing")));
    }

    [Fact]
    public void A_folder_already_on_disk_counts_as_taken_even_when_no_conversation_owns_it()
    {
        // A person made pricing/ by hand. The conversation called pricing must not adopt its files.
        Assert.Equal("pricing~2", ConversationFolder.Choose("pricing", Taken("pricing")));
    }

    [Fact]
    public void The_lowest_free_suffix_is_used()
    {
        Assert.Equal("con~4", ConversationFolder.Choose("con", Taken("con~2", "CON~3")));
    }

    [Fact]
    public void A_suffixed_folder_cannot_be_another_conversations_own_name()
    {
        // The suffix is safe only because an identifier cannot contain '~' (§9.2).
        Assert.DoesNotMatch("^[a-zA-Z0-9._-]+$", ConversationFolder.Choose("con", Taken()));
    }
}
