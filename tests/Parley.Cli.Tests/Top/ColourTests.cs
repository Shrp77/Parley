using Parley.Cli.Top;
using Parley.Core;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That nothing this surface draws means anything by colour alone.
/// </summary>
/// <remarks>
/// <para>
/// SC-007 and FR-005. This matters more in a terminal than in a browser, because a terminal may
/// genuinely have no colour: a pipe, a CI log, PuTTY on a bad day, or a reader who cannot
/// distinguish the two the surface happened to pick.
/// </para>
/// <para>
/// <b>Rendered and read back, not inspected.</b> Reading the source for colour tags would check
/// that somebody wrote the markup they meant to; it would say nothing about whether a fact
/// survives without it. So these render, strip everything a terminal would have interpreted, and
/// assert on the text that is left — which is exactly what a reader without colour sees.
/// </para>
/// </remarks>
public sealed class ColourTests
{
    [Theory]
    [InlineData(AgentPresence.Running, AgentStatus.Active, "running")]
    [InlineData(AgentPresence.Startable, AgentStatus.Active, "startable")]
    [InlineData(AgentPresence.Stranded, AgentStatus.Active, "stranded")]
    [InlineData(AgentPresence.Running, AgentStatus.Retired, "retired")]
    public void An_agents_state_reads_the_same_without_colour(
        AgentPresence presence, AgentStatus status, string expected)
    {
        // The four states that matter, and the one distinction the spec calls out by name:
        // startable and stranded both look like "not here", and only one of them means a peer
        // cannot be asked. A surface that separated them by colour alone would collapse them for
        // every reader who has none.
        var plain = Plain(new Markup(AgentsView.State(Agent(presence, status))));

        Assert.Equal(expected, plain.Trim());
    }

    [Fact]
    public void An_agent_that_stated_nothing_says_so_in_words()
    {
        // An absent claim is a fact, not a gap. Rendering it as an empty dim cell would read as a
        // column that failed rather than as something the agent never said.
        var plain = Plain(new Markup(AgentsView.Owns(Agent(AgentPresence.Running, AgentStatus.Active))));

        Assert.Equal("not stated", plain.Trim());
    }

    [Fact]
    public void Who_did_not_see_a_message_is_carried_as_text()
    {
        // The annotation a reader trusts, and the one thing on a transcript that cannot be
        // recovered from anywhere else on the screen. Three participants and a pairwise message
        // means exactly one of them was not party to it, and their name has to appear.
        var messages = new[]
        {
            Message("orders@here", "billing@here", 1),
            Message("orders@here", "inventory@here", 2),
        };

        var participants = Parley.Client.Rendering.Visibility.Participants(messages);

        var plain = Plain(TranscriptView.Panel(
            messages[0], participants, new Dictionary<long, ExchangeSnapshot>()));

        Assert.Contains("inventory@here", plain, StringComparison.Ordinal);
        Assert.Contains("did not see this", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broker_that_disagrees_about_the_version_names_both_numbers()
    {
        // "Incompatible" without the two numbers cannot be acted on, and a colour saying
        // "something is wrong here" is the version of that with even less information.
        var plain = Plain(new Markup(
            ProjectList.Describe(new Parley.Client.ProjectHealth("99.9", null))));

        Assert.Contains("99.9", plain, StringComparison.Ordinal);
        Assert.Contains(ProtocolVersion.Current, plain, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broker_that_is_not_answering_says_so_rather_than_going_red()
    {
        var plain = Plain(new Markup(
            ProjectList.Describe(new Parley.Client.ProjectHealth(null, "connection refused"))));

        Assert.Contains("not answering", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_cannot_style_the_terminal_it_is_read_in()
    {
        // The injection. A payload is agent-written and may contain anything; markup characters in
        // one would otherwise let a sender choose how their own words are drawn, or break the
        // frame around them -- the terminal's version of what the console refuses by never
        // emitting raw output.
        var hostile = Message(
            "orders@here",
            "billing@here",
            1,
            payload: """{"said":"[red]not really red[/] [[and this is not a tag]]"}""");

        var plain = Plain(TranscriptView.Panel(
            hostile,
            [AgentName.Create("orders@here"), AgentName.Create("billing@here")],
            new Dictionary<long, ExchangeSnapshot>()));

        // The tag survives as characters a reader can see, rather than as styling nobody asked
        // for. If it had been interpreted, this text would be absent from the output entirely.
        Assert.Contains("[red]", plain, StringComparison.Ordinal);
        Assert.Contains("not really red", plain, StringComparison.Ordinal);
    }

    /// <summary>Renders something the way a terminal with no colour at all would.</summary>
    /// <param name="renderable">What to draw.</param>
    /// <returns>The text a reader without colour sees.</returns>
    /// <remarks>
    /// <para>
    /// Colour switched off <i>and</i> the escapes stripped, which is belt and braces on purpose.
    /// </para>
    /// <para>
    /// <b>An earlier version did only the first, and CI caught it on all three platforms.</b> The
    /// reasoning it carried was that stripping merely proves the codes can be removed, while
    /// switching colour off asks the renderer the reader's own question. That was wrong about the
    /// mechanism: <c>dim</c> is a <i>decoration</i> and not a colour, so
    /// <see cref="ColorSystemSupport.NoColors"/> does not remove it — the output still carried a
    /// dim sequence, and whether it did depended on the environment, which is exactly why it
    /// passed on one machine and failed on every runner.
    /// </para>
    /// <para>
    /// The stripping is what makes this deterministic. What a reader without colour sees is the
    /// text with the control sequences gone, and that is now what is asserted rather than whatever
    /// the profile happened to negotiate.
    /// </para>
    /// </remarks>
    private static string Plain(IRenderable renderable)
    {
        var written = new StringWriter();

        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(written),
        });

        console.Profile.Width = 160;
        console.Write(renderable);

        return Bare(written.ToString());
    }

    /// <summary>Removes every control sequence, leaving what a reader reads.</summary>
    /// <param name="text">Rendered output.</param>
    /// <returns>The same text with nothing in it a terminal would interpret.</returns>
    private static string Bare(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text,
            Csi,
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(5));

    /// <summary>
    /// A control sequence: the escape character, a bracket, some parameters, and a letter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Anchored to the escape character, and that is load-bearing rather than tidy.</b> A
    /// pattern matching a bare bracket followed by a letter would eat the literal markup tag that
    /// <see cref="A_message_cannot_style_the_terminal_it_is_read_in"/> exists to find still
    /// sitting in the output — so the stripper would manufacture the very evidence that the
    /// injection had been prevented.
    /// </para>
    /// <para>
    /// The escape is spelled as a unicode escape rather than typed. A raw control byte in a source
    /// file is invisible in most editors and survives edits nobody intended, which this repository
    /// has already had happen once in a test.
    /// </para>
    /// </remarks>
    private static readonly string Csi = ((char)27) + @"\[[0-9;]*[A-Za-z]";

    /// <summary>An agent in a given state.</summary>
    /// <param name="presence">Whether it is there.</param>
    /// <param name="status">Whether its registration stands.</param>
    /// <returns>The record.</returns>
    private static AgentRecord Agent(AgentPresence presence, AgentStatus status) =>
        new(AgentName.Create("orders@here"),
            "claude-code",
            AgentTier.Pull,
            Workspace: null,
            SessionRef: null,
            AdapterConfig: null,
            Connected: presence == AgentPresence.Running,
            status,
            Owns: null,
            presence);

    /// <summary>One message in a conversation.</summary>
    /// <param name="from">Who sent it.</param>
    /// <param name="to">Who it was for.</param>
    /// <param name="id">Its identifier.</param>
    /// <param name="payload">What it carried.</param>
    /// <returns>The message.</returns>
    private static Message Message(string from, string to, long id, string? payload = null) =>
        new(id,
            ExchangeId: null,
            MessageType.Question,
            AgentName.Create(from),
            AgentName.Create(to),
            "test",
            payload ?? """{"said":"a question"}""",
            "conversation",
            DateTimeOffset.UnixEpoch,
            null);
}
