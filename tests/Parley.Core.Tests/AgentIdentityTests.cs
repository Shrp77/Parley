using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// Which agent this is, as distinct from what any project calls it.
/// </summary>
/// <remarks>
/// The properties worth holding are all negative — that nothing derives it, that a value this cannot read is
/// refused rather than guessed at, and that there is one spelling. Each of them is a way the identity could
/// quietly stop being stable, which is the only thing it is for.
/// </remarks>
public sealed class AgentIdentityTests
{
    [Fact]
    public void Minting_twice_gives_two_identities()
    {
        Assert.NotEqual(AgentIdentity.Mint(), AgentIdentity.Mint());
    }

    [Fact]
    public void What_was_minted_reads_back_as_the_same_identity()
    {
        // It is written into two files on every machine that catalogues it, so a round trip that lost
        // anything would attach a repository to the wrong entry.
        var minted = AgentIdentity.Mint();

        Assert.True(AgentIdentity.TryRead(minted.ToString(), out var read));
        Assert.Equal(minted, read);
    }

    [Fact]
    public void There_is_one_spelling_of_it()
    {
        // A catalogue written with braces and read without them is a catalogue whose entries cannot be
        // found, and that failure looks like an agent nobody ever enrolled.
        var written = AgentIdentity.Mint().ToString();

        Assert.Equal(36, written.Length);
        Assert.Equal(written.ToLowerInvariant(), written);
        Assert.DoesNotContain('{', written);
        Assert.Equal(4, written.Count(c => c == '-'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("orders")]
    [InlineData("orders@here")]
    [InlineData("C:/DevHell/Github/orders")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("9f2c1a773b414e0e8a6f7c4b2d9e5011")]
    [InlineData("{9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011}")]
    public void Anything_that_is_not_one_is_refused_rather_than_guessed_at(string? candidate)
    {
        // Including the empty identity, which is what a default-constructed value reads as: a catalogue key
        // of all zeroes would be one entry every un-enrolled repository matched.
        Assert.False(AgentIdentity.TryRead(candidate, out _));
        Assert.Throws<FormatException>(() => AgentIdentity.Read(candidate));
    }

    [Fact]
    public void A_refusal_shows_what_one_looks_like()
    {
        // The remedy is to write a different value, so the message has to carry the shape rather than
        // describe it.
        var refused = Assert.Throws<FormatException>(() => AgentIdentity.Read("orders"));

        Assert.Contains("orders", refused.Message, StringComparison.Ordinal);
        Assert.Matches("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", refused.Message);
    }

    [Fact]
    public void A_default_identity_knows_it_is_not_one()
    {
        Assert.False(default(AgentIdentity).Known);
        Assert.True(AgentIdentity.Mint().Known);
    }

    [Fact]
    public void Nothing_derives_one_from_a_path_a_name_a_project_or_a_machine()
    {
        // **The whole point, asserted as a surface rather than as behaviour.** A deriving overload is how
        // this stops being stable: somebody adds `For(workspace)` because it is convenient, and a moved
        // checkout silently becomes a second agent. There is one way to get a new one, and it takes nothing.
        var made = typeof(AgentIdentity)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(AgentIdentity))
            .ToList();

        var minting = Assert.Single(made, method => method.Name == nameof(AgentIdentity.Mint));

        Assert.Empty(minting.GetParameters());

        // Everything else that hands one back reads one that already existed.
        Assert.All(
            made.Where(method => method != minting),
            method => Assert.Equal(nameof(AgentIdentity.Read), method.Name));
    }
}
