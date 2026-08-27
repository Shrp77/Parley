using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// How to start a repository, and whether a supervisor may, are two different facts.
/// </summary>
/// <remarks>
/// <para>
/// The schema has always kept two columns and its trigger enforces only that consent implies a
/// command, never the reverse. This server nonetheless wrote consent whenever a command was
/// present, so nobody configuring a repository could hold them apart.
/// </para>
/// <para>
/// The cost landed at configuration time, which is why it survived so long: recording how to
/// start a repository became a question about spending the operator's tokens unwatched, the
/// honest answer was usually no, and the deployment ended up unable to start anything at all --
/// including from an operator's own console, with a person watching, which was never what was
/// being consented to.
/// </para>
/// </remarks>
public sealed class InvocationConsentTests
{
    private static ServerOptions Read(params (string Name, string Value)[] set)
    {
        var environment = set.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);

        return ServerOptions.Read(name =>
            environment.TryGetValue(name, out var value) ? value : null);
    }

    private static (string, string)[] Minimum =>
    [
        (ServerOptions.DatabaseVariable, "parley.db"),
        (ServerOptions.AgentVariable, "orders")
    ];

    [Fact]
    public void A_command_alone_grants_nothing()
    {
        // The whole change in one assertion. A repository that records how to start itself has
        // said an operator may start it, which is what opening the session by hand already does.
        // It has not said a supervisor may, with nobody there.
        var options = Read([.. Minimum, (ServerOptions.InvokeCommandVariable, "claude")]);

        Assert.Equal("claude", options.InvokeCommand);
        Assert.False(options.InvokeWhenIdle);
    }

    [Fact]
    public void The_permission_is_its_own_line()
    {
        var options = Read([
            .. Minimum,
            (ServerOptions.InvokeCommandVariable, "claude"),
            (ServerOptions.InvokeWhenIdleVariable, "true")
        ]);

        Assert.Equal("claude", options.InvokeCommand);
        Assert.True(options.InvokeWhenIdle);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData(" true ")]
    public void A_launch_configuration_is_written_by_hand_so_yes_has_spellings(string granted) =>
        Assert.True(Read([
            .. Minimum,
            (ServerOptions.InvokeCommandVariable, "claude"),
            (ServerOptions.InvokeWhenIdleVariable, granted)
        ]).InvokeWhenIdle);

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData("TRUE!")]
    public void Anything_unrecognised_is_no(string refused)
    {
        // The safe reading of a value nobody anticipated is that permission was not granted.
        // Getting this backwards would let a typo spend somebody's tokens.
        Assert.False(Read([
            .. Minimum,
            (ServerOptions.InvokeCommandVariable, "claude"),
            (ServerOptions.InvokeWhenIdleVariable, refused)
        ]).InvokeWhenIdle);
    }

    [Fact]
    public void Consent_without_a_command_is_not_carried_forward()
    {
        // The schema refuses this by trigger -- consent with nothing to run is a candidate that
        // fails silently on every pass -- so the server must not present it. Dropping the grant
        // is the right direction: the alternative is a launch that aborts on a mistake nobody
        // can see from the error.
        var options = Read([.. Minimum, (ServerOptions.InvokeWhenIdleVariable, "true")]);

        Assert.Null(options.InvokeCommand);
        Assert.True(options.InvokeWhenIdle);
    }

    [Fact]
    public void Setup_writes_the_command_and_asks_only_about_the_permission()
    {
        // Pinned against the command file, because this is where the old conflation actually hurt
        // people: setup treated recording a command as a permission question, so most
        // repositories ended up with neither and could not be started at all.
        var setup = RepositoryFile.Read("plugins/parley/commands/setup.md");

        Assert.Contains("\"PARLEY_INVOKE_COMMAND\": \"claude\"", setup, StringComparison.Ordinal);
        Assert.Contains("write it without asking", setup, StringComparison.Ordinal);
        Assert.Contains("PARLEY_INVOKE_WHEN_IDLE", setup, StringComparison.Ordinal);
        Assert.Contains("take no for an answer", setup, StringComparison.Ordinal);
    }
}
