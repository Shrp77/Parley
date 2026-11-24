using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>The further host names the web console answers to: <c>webUi.hosts</c> in <c>&lt;root&gt;/settings.json</c>.</summary>
/// <remarks>
/// <b>The owner, 2026-11-22:</b> a Tailscale <c>serve</c> address forwarded to this machine's loopback console was refused
/// as a bad host. The host check is the DNS rebinding defence, so only a whole name the person wrote is taken: never a
/// wildcard or an address, which would let a name an attacker points at 127.0.0.1 through.
/// </remarks>
[Integration]
public sealed class WebUiHostsSettingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-webui-hosts-" + Path.GetRandomFileName());

    public WebUiHostsSettingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private IReadOnlyList<string> Read(string json, out IReadOnlyList<string> refused)
    {
        File.WriteAllText(Path.Combine(_root, ParleySettings.FileName), json);

        return ParleySettings.ReadWebUiHosts(_root, out refused);
    }

    [Fact]
    public void With_no_file_no_name_is_added()
    {
        Assert.Empty(ParleySettings.ReadWebUiHosts(_root, out var refused));
        Assert.Empty(refused);
    }

    [Fact]
    public void A_listed_name_is_taken_in_lower_case_and_once()
    {
        var names = Read(
            """{ "inbox": { "closedDays": 3 }, "webUi": { "hosts": [ "WorkStation.tail0000.ts.net", " workstation.tail0000.ts.net ", "console.example" ] } }""",
            out var refused);

        Assert.Equal(["workstation.tail0000.ts.net", "console.example"], names);
        Assert.Empty(refused);
    }

    [Theory]
    [InlineData("\"*\"")]
    [InlineData("\"*.ts.net\"")]
    [InlineData("\".ts.net\"")]
    [InlineData("\"ts.net.\"")]
    [InlineData("\"192.168.1.5\"")]
    [InlineData("\"[::1]\"")]
    [InlineData("\"host:5280\"")]
    [InlineData("\"http://host.example\"")]
    [InlineData("\"two words.example\"")]
    [InlineData("\"-lead.example\"")]
    [InlineData("\"trail-.example\"")]
    [InlineData("\"under_score.example\"")]
    [InlineData("\"bücher.example\"")]
    [InlineData("\"\"")]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("[\"nested.example\"]")]
    public void Anything_but_a_whole_host_name_is_left_out_and_said(string entry)
    {
        var names = Read($$"""{ "webUi": { "hosts": [ {{entry}}, "kept.example" ] } }""", out var refused);

        Assert.Equal(["kept.example"], names);
        Assert.Single(refused);
    }

    [Theory]
    [InlineData("""{ "webUi": { "hosts": "one.example" } }""")]
    [InlineData("""{ "webUi": { "hosts": { "a": "one.example" } } }""")]
    public void A_hosts_value_that_is_not_a_list_adds_nothing_and_says_so(string json)
    {
        Assert.Empty(Read(json, out var refused));
        Assert.Single(refused);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "webUi": { "hosts": [ "one.example" ], "hosts": [ "two.example" ] } }""")]
    public void A_file_that_cannot_be_read_adds_nothing_and_says_so(string json)
    {
        Assert.Empty(Read(json, out var refused));
        Assert.Single(refused);
    }

    [Fact]
    public void At_most_sixteen_names_are_taken()
    {
        var listed = string.Join(", ", Enumerable.Range(1, ParleySettings.MostWebUiHosts + 2).Select(n => $"\"h{n}.example\""));

        var names = Read($$"""{ "webUi": { "hosts": [ {{listed}} ] } }""", out var refused);

        Assert.Equal(ParleySettings.MostWebUiHosts, names.Count);
        Assert.Equal(2, refused.Count);
    }
}
