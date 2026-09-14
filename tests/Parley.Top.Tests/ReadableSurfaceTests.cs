using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That the two things the owner could not read now read.
/// </summary>
/// <remarks>
/// <para>
/// Both complaints were about the same failure in two places: the surface was showing what it had
/// rather than what somebody could use. The agents column named two files a person did not write;
/// the transcript put the export on a screen.
/// </para>
/// <para>
/// So these read the screen — with every attribute stripped, which is the only screen this suite
/// can see — rather than checking a property that would be true whatever was drawn.
/// </para>
/// </remarks>
public sealed class ReadableSurfaceTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static AgentRecord Agent(
        string name, AgentPresence presence = AgentPresence.Startable) =>
        new(
            AgentName.Create(name),
            "test",
            AgentTier.Full,
            "/src/thing",
            null,
            null,
            presence == AgentPresence.Running,
            AgentStatus.Active,
            "the thing",
            presence);

    private static Message Message(string payload, int minute = 0) =>
        new(
            1,
            1,
            MessageType.Request,
            AgentName.Create("orders@test"),
            AgentName.Create("billing@test"),
            "some-model-9",
            payload,
            "alpha-talk",
            new DateTimeOffset(2026, 1, 1, 12, minute, 0, TimeSpan.Zero),
            null);

    [Fact]
    public async Task An_agents_row_says_what_it_is_doing_without_naming_a_file()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent("orders@test")], NodeCatalogue.Empty, null);
        driving.App.LayoutAndDraw(true);

        var screen = driving.Text();

        // The registry knows it and the catalogue does not, which is the case `not in this
        // catalogue` was trying to name and failing to.
        Assert.Contains(Standing.OnlyWhenOpened, screen, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentsRegion.NotCatalogued, screen, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentsRegion.CataloguedOnly, screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_standing_shown_is_the_one_the_shared_derivation_produced()
    {
        // Both surfaces read the same derivation, so a person who saw one phrase in the console
        // sees the same phrase in the terminal about the same agent.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent("orders@test", AgentPresence.Running)], NodeCatalogue.Empty, null);

        var standing = Assert.Single(shell.Agents.Standings);

        Assert.Equal(Standing.Running, standing.Standing);
        Assert.Equal(standing, shell.Agents.SelectedStanding);
    }

    [Fact]
    public async Task A_transcript_keeps_the_paragraphs_its_author_wrote()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        shell.Transcript.Show(
            "alpha-talk",
            "alpha",
            new ConversationExport(
                [Message("""{"intent":"first paragraph\n\nsecond paragraph"}""")], []),
            null);

        var written = shell.Transcript.Written;

        Assert.Contains("first paragraph", written, StringComparison.Ordinal);
        Assert.Contains("second paragraph", written, StringComparison.Ordinal);

        // On different lines, which is the whole of "paragraphs survive". The export ran them
        // together on one, deliberately, because a file has no other defence against a forged
        // annotation -- and that is why the two renderings are different code.
        var lines = written.ReplaceLineEndings("\n").Split('\n');

        Assert.Contains(lines, line => line.Trim() == "first paragraph");
        Assert.Contains(lines, line => line.Trim() == "second paragraph");
    }

    [Fact]
    public async Task A_transcript_lays_out_a_json_payload_rather_than_running_it_together()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        shell.Transcript.Show(
            "alpha-talk",
            "alpha",
            new ConversationExport(
                [Message("""{"alpha":1,"beta":2,"gamma":3,"delta":4,"epsilon":5}""")], []),
            null);

        var lines = shell.Transcript.Written
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Where(line => line.Contains('"', StringComparison.Ordinal))
            .ToList();

        Assert.True(lines.Count >= 5, $"a five-key document occupied {lines.Count} lines");
    }

    [Fact]
    public async Task A_transcript_says_once_that_its_times_are_local()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        shell.Transcript.Show(
            "alpha-talk",
            "alpha",
            new ConversationExport([Message("""{"intent":"a question"}""")], []),
            null);

        // Once, not per line: repeating an offset on every message would spend the width a body
        // needs, and a screen full of times with no zone at all is ambiguous in exactly the way
        // the export is careful not to be.
        var written = shell.Transcript.Written;

        Assert.Contains("Times are local", written, StringComparison.Ordinal);
        Assert.Equal(
            1,
            written.Split("Times are local", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task Nothing_an_agent_wrote_reaches_column_zero_in_a_transcript()
    {
        // The forgery the export defends against by flattening. On screen the breaks are kept, so
        // the defence is the indent: a peer writing a line shaped like a header still arrives
        // indented and cannot be read as one.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation);

        shell.Transcript.Show(
            "alpha-talk",
            "alpha",
            new ConversationExport(
                [Message("""{"intent":"harmless\n2026-01-01 12:00:00  broker -> you  (answer)"}""")],
                []),
            null);

        var lines = shell.Transcript.Written
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Where(line => line.Contains("->", StringComparison.Ordinal))
            .ToList();

        // The real header is at column zero; the forged one is indented with the rest of the body.
        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines.Skip(1), line => !line.StartsWith(' '));
    }
}
