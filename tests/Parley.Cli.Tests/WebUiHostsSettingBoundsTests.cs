using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>The bounds on <c>webUi.hosts</c>, held by number rather than by the constant they guard.</summary>
/// <remarks>
/// <see cref="WebUiHostsSettingTests"/> counted against <see cref="ParleySettings.MostWebUiHosts"/>, so a cap raised to any
/// number passed; and no case named a label past 63 characters or a name past 253, so either bound could go unnoticed
/// (review, lens 3 surface A on d99806d9). The documentation says sixteen.
/// </remarks>
[Integration]
public sealed class WebUiHostsSettingBoundsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-webui-bounds-" + Path.GetRandomFileName());

    public WebUiHostsSettingBoundsTests() => Directory.CreateDirectory(_root);

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

    private IReadOnlyList<string> Read(IEnumerable<string> hosts, out IReadOnlyList<string> refused)
    {
        var listed = string.Join(", ", hosts.Select(host => $"\"{host}\""));

        File.WriteAllText(Path.Combine(_root, ParleySettings.FileName), $$"""{ "webUi": { "hosts": [ {{listed}} ] } }""");

        return ParleySettings.ReadWebUiHosts(_root, out refused);
    }

    [Fact]
    public void Sixteen_names_are_taken_and_the_seventeenth_is_left_out()
    {
        var names = Read(Enumerable.Range(1, 17).Select(n => $"h{n}.example"), out var refused);

        Assert.Equal(16, names.Count);
        Assert.Equal("h16.example", names[^1]);
        Assert.Contains("\"h17.example\" was left out", Assert.Single(refused), StringComparison.Ordinal);
    }

    [Fact]
    public void A_label_of_sixty_three_is_taken_and_one_of_sixty_four_is_left_out()
    {
        var longest = new string('a', 63) + ".example";
        var over = new string('b', 64) + ".example";

        var names = Read([longest, over], out var refused);

        Assert.Equal([longest], names);
        Assert.Single(refused);
    }

    [Fact]
    public void A_name_of_two_hundred_and_fifty_three_is_taken_and_one_longer_is_left_out()
    {
        // Four labels of 63 with three dots is 255; trimmed to 253 and 254 with every label still legal.
        var label = new string('a', 63);
        var longest = string.Join('.', label, label, label, new string('a', 61));
        var over = string.Join('.', label, label, label, new string('b', 62));

        Assert.Equal(253, longest.Length);
        Assert.Equal(254, over.Length);

        var names = Read([longest, over], out var refused);

        Assert.Equal([longest], names);
        Assert.Single(refused);
    }
}
