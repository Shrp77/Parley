using Xunit;

namespace Parley.Web.Tests;

/// <summary>Which stretches of a message become links: web addresses only, and nothing is lost.</summary>
[Unit]
public sealed class LinkedTextTests
{
    [Fact]
    public void A_web_address_is_a_link_and_the_full_stop_after_it_is_not()
    {
        var parts = LinkedText.Split("See https://example.com/a?b=1. Then go.");

        Assert.Equal("https://example.com/a?b=1", parts.Single(p => p.Link is not null).Text);
        Assert.Equal("See https://example.com/a?b=1. Then go.", string.Concat(parts.Select(p => p.Text)));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("ftp://example.com/x")]
    [InlineData("https://")]
    public void Anything_but_http_and_https_stays_plain_words(string text)
    {
        Assert.All(LinkedText.Split(text), part => Assert.Null(part.Link));
    }
}
