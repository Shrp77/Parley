using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That the conversation page offers who to write to as a tree grouped by machine, and the quick filter that narrows
/// the transcript.
/// </summary>
/// <remarks>
/// The boxes sit in the side column and belong to the composer's form, which is what moved the row of chips out of the
/// bottom bar. The filter itself is script and is checked in a browser; what is pinned here is that the page carries
/// what the script reads.
/// </remarks>
[Integration]
public sealed class RecipientTreeTests
{
    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    private static Wire.RegistryRow Row(string name, string adapter = "claude-code") =>
        new()
        {
            Name = name,
            Adapter = adapter,
            Tier = Wire.AgentTier.Full,
            Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running,
        };

    private static TranscriptConsole Project()
    {
        var console = new TranscriptConsole();

        console.Broker.Request(1, "orders@beta", "human-user@alpha", "Which month do you want?");
        console.Broker.Registry.AddRange(
        [
            Row("orders@beta"), Row("billing@beta"), Row("ledger@gamma"),
            Row("human-user@alpha", Parley.Core.PersonSeat.Adapter),
        ]);

        return console;
    }

    [Fact]
    public async Task Under_a_machine_the_agents_sit_in_a_subgroup_for_their_state_rather_than_each_saying_it()
    {
        using var console = Project();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);
        var to = page[page.IndexOf("aria-label=\"Who to talk to\"", StringComparison.Ordinal)..];

        Assert.Contains("data-group=\"beta/", to, StringComparison.Ordinal);
        Assert.Contains("class=\"group subgroup\"", to, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"state\">", to, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agents_are_grouped_by_machine_with_the_persons_group_first_and_named_by_role()
    {
        using var console = Project();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        var alpha = page.IndexOf("data-group=\"alpha\"", StringComparison.Ordinal);
        var beta = page.IndexOf("data-group=\"beta\"", StringComparison.Ordinal);
        var gamma = page.IndexOf("data-group=\"gamma\"", StringComparison.Ordinal);

        Assert.True(alpha >= 0, "the person's group is missing");
        Assert.True(alpha < beta && beta < gamma, "groups are the person's first, then by name");

        // The role alone, because the group says the machine: that is what stopped a name wrapping.
        Assert.Contains(">orders</a>", page, StringComparison.Ordinal);
        Assert.DoesNotContain(">orders@beta</a>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_boxes_belong_to_the_composer_and_the_chip_row_is_gone()
    {
        using var console = Project();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        Assert.Contains("<form method=\"post\" id=\"composer\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Targets\" value=\"ledger@gamma\"", page, StringComparison.Ordinal);
        Assert.Contains("form=\"composer\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"targets\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_carries_what_the_filter_reads()
    {
        using var console = Project();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);

        // Who "me" is, and who each message went to as well as who sent it.
        Assert.Contains("id=\"filter\"", page, StringComparison.Ordinal);
        Assert.Contains("data-human=\"human-user@alpha\"", page, StringComparison.Ordinal);
        Assert.Contains("data-to=\"human-user@alpha\"", page, StringComparison.Ordinal);
    }
}
