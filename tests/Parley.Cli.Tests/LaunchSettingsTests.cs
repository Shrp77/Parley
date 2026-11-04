using Parley.Configuration;
using Parley.Tests;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>A machine's block over a launch configuration (SPEC.md §4.1.2), from the shared table.</summary>
[Unit]
public sealed class LaunchSettingsTests
{
    /// <summary>The table's names, which are what a failure reports.</summary>
    public static TheoryData<string> Cases => [.. LaunchTable.Cases.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_reader_reaches_what_the_table_says(string name)
    {
        var expected = LaunchTable.Cases.Single(c => c.Name == name);

        var read = LaunchConfiguration.TryParse(expected.File, "the file", out var configuration, out var problem, out var broken);

        if (expected.Problem is { } carried)
        {
            Assert.False(read);
            Assert.True(broken);
            Assert.Contains(carried, problem, StringComparison.Ordinal);

            return;
        }

        Assert.True(read, problem);
        Assert.False(broken);

        var block = configuration.BlockFor(expected.Machine, expected.Key);

        Assert.Equal(expected.Owns, block?.Owns);
        Assert.Equal(expected.Broker, block?.Broker);
    }

    [Fact]
    public void A_setting_a_block_does_not_give_comes_from_the_launch()
    {
        var located = new LocatedLaunch(
            "folder",
            Parsed("""{ "laptop": { "parley-acme": { "PARLEY_BROKER": "http://127.0.0.1:19100" } } }"""),
            new LaunchMembership("acme", "infra", "parley-acme"),
            null);

        var settings = located.SettingsFor("laptop", name => name == "PARLEY_OWNS" ? "From the launch." : "http://wrong:1");

        Assert.Equal("From the launch.", settings.Owns);
        Assert.Equal("http://127.0.0.1:19100", settings.Broker);
        Assert.True(settings.BrokerFromBlock);
    }

    [Fact]
    public void A_block_beats_what_the_launch_was_given()
    {
        var located = new LocatedLaunch(
            "folder",
            Parsed("""{ "laptop": { "parley-acme": { "PARLEY_OWNS": "The block's." } } }"""),
            new LaunchMembership("acme", "infra", "parley-acme"),
            null);

        var settings = located.SettingsFor("laptop", name => name == "PARLEY_OWNS" ? "A person's variable." : null);

        Assert.Equal("The block's.", settings.Owns);
        Assert.False(settings.BrokerFromBlock);
    }

    [Fact]
    public void Which_entry_a_launch_is_comes_from_its_role_and_project()
    {
        var configuration = Parsed("{ }");

        Assert.Equal("parley-beta", configuration.EntryFor("infra", "beta")?.Key);
        Assert.Equal("parley-acme", configuration.EntryFor("infra@laptop", "acme")?.Key);
        Assert.Null(configuration.EntryFor("infra", null));
        Assert.Null(configuration.EntryFor("orders", "acme"));
    }

    private static LaunchConfiguration Parsed(string section)
    {
        Assert.True(LaunchConfiguration.TryParse(
            """
            { "mcpServers": {
                "parley-acme": { "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "acme" } },
                "parley-beta": { "env": { "PARLEY_AGENT": "infra", "PARLEY_PROJECT": "beta" } } },
              "parleyMachines":
            """ + section + " }",
            "the file",
            out var configuration,
            out var problem,
            out _), problem);

        return configuration;
    }
}
