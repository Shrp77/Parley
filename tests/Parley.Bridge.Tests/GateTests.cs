using Xunit;

namespace Parley.Bridge.Tests;

/// <summary>
/// The gate in front of every endpoint.
/// </summary>
/// <remarks>
/// The sharpest edge in the project. This process can start agents and write to their standard
/// input, which is arbitrary instruction of a program holding tool permissions in a real
/// repository. Everything here is a probe rather than a reading: the failure mode is a control
/// that looks present and admits the request anyway.
/// </remarks>
public sealed class GateTests
{
    private const string Origin = "http://127.0.0.1:7777";

    [Fact]
    public void A_request_with_no_token_is_refused()
    {
        // The whole attack in one line. Loopback is not a security boundary for a web
        // application: any page in any tab can issue a request to 127.0.0.1, and a bridge that
        // answered on the strength of the address alone would let a visited advertisement retire
        // an agent. The classic form does not need to read the reply to do the damage.
        Assert.False(AccessToken.Admits(null, "secret", null, Origin));
        Assert.False(AccessToken.Admits("", "secret", null, Origin));
    }

    [Fact]
    public void A_request_with_the_wrong_token_is_refused()
    {
        Assert.False(AccessToken.Admits("secre", "secret", null, Origin));
        Assert.False(AccessToken.Admits("secrets", "secret", null, Origin));
        Assert.False(AccessToken.Admits("SECRET", "secret", null, Origin));
    }

    [Fact]
    public void A_valid_token_from_another_origin_is_still_refused()
    {
        // The control that matters most, and the one it is tempting to leave out because the
        // token "already covers it". It does not: the two failures are independent. A token can
        // leak -- into a screenshot, a shell history, a pasted URL -- and the origin check is
        // what stands between a leaked token and a page that knows it.
        Assert.False(AccessToken.Admits("secret", "secret", "https://evil.example", Origin));
        Assert.False(AccessToken.Admits("secret", "secret", "http://localhost:7777", Origin));
    }

    [Fact]
    public void The_bridge_own_page_is_admitted()
    {
        Assert.True(AccessToken.Admits("secret", "secret", Origin, Origin));

        // Case-insensitively, because a browser may normalise the scheme and host and this is
        // not the check that should reject a request over capitalisation.
        Assert.True(AccessToken.Admits("secret", "secret", "HTTP://127.0.0.1:7777", Origin));
    }

    [Fact]
    public void A_caller_that_is_not_a_browser_is_admitted_on_its_token()
    {
        // curl, a script, a test. They send no Origin and were never the threat: anything that
        // can set one header can set any header, so refusing them would cost the operator their
        // shell without stopping anybody. What the origin check exists to stop is a page, and a
        // page always sends it.
        Assert.True(AccessToken.Admits("secret", "secret", null, Origin));
    }

    [Fact]
    public void Two_tokens_are_never_the_same()
    {
        // A token that repeated across runs would outlive the process it authorised, which is
        // the property a stored token has and this deliberately does not.
        var minted = Enumerable.Range(0, 64).Select(_ => AccessToken.Create()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(64, minted.Count);
        Assert.All(minted, t => Assert.True(t.Length >= 40));

        // URL-safe: it is printed into a link the operator clicks, and a '+' or '/' in a query
        // string is a token that arrives at the server as something else.
        Assert.All(minted, t => Assert.DoesNotContain('+', t));
        Assert.All(minted, t => Assert.DoesNotContain('/', t));
        Assert.All(minted, t => Assert.DoesNotContain('=', t));
    }

    [Fact]
    public void The_bridge_listens_only_to_this_machine_unless_told_otherwise()
    {
        // The default that matters. Getting this wrong exposes remote code execution in every
        // repository the bridge can reach, so widening it is a flag somebody has to type, spelled
        // so that nobody sets it thinking it names which machine to connect to.
        var (quiet, _) = BridgeOptions.Parse(["--db", "x.db"]);

        Assert.NotNull(quiet);
        Assert.Equal(BridgeOptions.Loopback, quiet.Address);
        Assert.False(quiet.IsExposed);

        var (loud, _) = BridgeOptions.Parse(["--db", "x.db", "--listen-on-all-interfaces"]);

        Assert.NotNull(loud);
        Assert.True(loud.IsExposed);
    }

    [Fact]
    public void Every_run_gets_its_own_token()
    {
        var (first, _) = BridgeOptions.Parse(["--db", "x.db"]);
        var (second, _) = BridgeOptions.Parse(["--db", "x.db"]);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Token, second.Token);
    }

    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    [InlineData("--port", "http")]
    [InlineData("--wat", "")]
    public void A_command_line_that_does_not_parse_is_refused_rather_than_guessed(
        string flag, string value)
    {
        var (options, error) = BridgeOptions.Parse(
            value.Length == 0 ? ["--db", "x.db", flag] : ["--db", "x.db", flag, value]);

        Assert.Null(options);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_bridge_with_no_database_says_so_rather_than_watching_nothing()
    {
        // The failure this prevents is the quiet one: a bridge pointed at a path nothing wrote
        // would show an empty fleet, which is indistinguishable from a healthy deployment with
        // no traffic. Refusing at startup is the only place that can be told apart.
        var (options, error) = BridgeOptions.Parse([]);

        if (Environment.GetEnvironmentVariable("PARLEY_DB") is { Length: > 0 })
        {
            // The operator's shell already names one; that is the documented way to run it.
            Assert.NotNull(options);
            return;
        }

        Assert.Null(options);
        Assert.Contains("PARLEY_DB", error, StringComparison.Ordinal);
    }
}
