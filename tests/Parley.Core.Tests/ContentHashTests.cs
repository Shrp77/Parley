using System.Text;
using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The content hash: what it is, and what it will read.
/// </summary>
/// <remarks>
/// The schema refuses anything but 64 lowercase hexadecimal characters, so the spelling is part of the contract
/// rather than a formatting choice.
/// </remarks>
public sealed class ContentHashTests
{
    [Fact]
    public void The_hash_of_empty_content_is_the_well_known_one()
    {
        // A zero-byte file can be placed, and this is what it lists with.
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ContentHash.Empty.Value);
    }

    [Fact]
    public void A_hash_is_64_lowercase_hexadecimal_characters()
    {
        var hash = ContentHash.Of(Encoding.UTF8.GetBytes("net of tax"));

        Assert.Equal(ContentHash.Length, hash.Value.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash.Value);
        Assert.Equal(hash.Value[..12], hash.Short);
    }

    [Fact]
    public void The_same_content_has_the_same_hash_and_different_content_a_different_one()
    {
        Assert.Equal(ContentHash.Of("a"u8), ContentHash.Of("a"u8));
        Assert.NotEqual(ContentHash.Of("a"u8), ContentHash.Of("b"u8));
    }

    [Fact]
    public void Text_a_person_copied_in_upper_case_reads_as_the_same_hash()
    {
        var hash = ContentHash.Of("a"u8);

        Assert.True(ContentHash.TryParse(hash.Value.ToUpperInvariant(), out var read));
        Assert.Equal(hash, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b8550")]
    public void Anything_else_is_not_a_hash(string? text)
    {
        Assert.False(ContentHash.TryParse(text, out _));
    }

    [Fact]
    public void A_digest_of_the_wrong_length_is_refused()
    {
        Assert.Throws<ArgumentException>(() => ContentHash.FromDigest(new byte[31]));
    }
}
