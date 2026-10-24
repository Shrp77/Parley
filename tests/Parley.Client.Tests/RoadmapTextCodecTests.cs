using Parley.Client.Roadmaps;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// A roadmap as editable text, and back (SPEC.md §5.7).
/// </summary>
public sealed class RoadmapTextCodecTests
{
    private static readonly AgentName Billing = AgentName.Create("billing@host");

    private static RoadmapContent Plan() => new("Billing export",
    [
        new RoadmapItemDraft(null, "Add CSV export", Billing, []),
        new RoadmapItemDraft(null, "Call it from the report", null, [1]),
        new RoadmapItemDraft(null, "Document the new column", null, [1, 2]),
    ]);

    [Fact]
    public void What_is_written_reads_back_as_the_same_content()
    {
        var (content, problem) = RoadmapTextCodec.Parse(RoadmapTextCodec.Write(Plan()));

        Assert.Null(problem);
        Assert.Equal("Billing export", content!.Title);
        Assert.Equal(["Add CSV export", "Call it from the report", "Document the new column"], content.Items.Select(i => i.Text));
        Assert.Equal(Billing, content.Items[0].IntendedAgent);
        Assert.Equal([1, 2], content.Items[2].Needs);
    }

    [Fact]
    public void The_written_text_has_the_shape_an_agent_proposes_in()
    {
        var text = RoadmapTextCodec.Write(Plan());

        Assert.Equal(
            "title: Billing export\n1. Add CSV export | for: billing@host\n2. Call it from the report | needs: 1\n3. Document the new column | needs: 1, 2",
            text);
    }

    [Fact]
    public void An_existing_items_identity_travels_so_an_edit_keeps_its_conversation()
    {
        var content = new RoadmapContent("Plan", [new RoadmapItemDraft(4, "Only", null, [])]);

        var text = RoadmapTextCodec.Write(content);
        var (back, problem) = RoadmapTextCodec.Parse(text);

        Assert.Contains("item: 4", text, StringComparison.Ordinal);
        Assert.Null(problem);
        Assert.Equal(4, back!.Items[0].Number);
    }

    [Fact]
    public void Moving_a_line_moves_the_item_and_the_numbers_still_say_what_each_one_needs()
    {
        // The labels name the items; the order of the lines is the order of the roadmap.
        var text = "title: Plan\n2. Second | needs: 1\n1. First";

        var (content, problem) = RoadmapTextCodec.Parse(text);

        // Second now comes first and needs the item that comes after it: the rules say so.
        Assert.NotNull(content);
        Assert.NotNull(problem);
        Assert.Equal(["Second", "First"], content.Items.Select(i => i.Text));
        Assert.Equal([2], content.Items[0].Needs);
    }

    [Fact]
    public void A_line_that_is_not_an_item_is_named_by_its_line()
    {
        var (content, problem) = RoadmapTextCodec.Parse("title: Plan\n1. Fine\nthis is not an item");

        Assert.Null(content);
        Assert.Contains("Line 3", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1. no title first")]
    public void A_roadmap_with_no_title_first_is_refused(string text)
    {
        var (content, problem) = RoadmapTextCodec.Parse(text);

        Assert.Null(content);
        Assert.NotNull(problem);
    }

    [Fact]
    public void An_agent_name_that_is_not_one_is_refused_on_its_line()
    {
        var (content, problem) = RoadmapTextCodec.Parse("title: Plan\n1. Do it | for: billing");

        Assert.Null(content);
        Assert.Contains("Line 2", problem, StringComparison.Ordinal);
        Assert.Contains("role@machine", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_lines_with_one_number_and_a_need_with_no_line_are_refused()
    {
        Assert.Contains("twice", RoadmapTextCodec.Parse("title: P\n1. A\n1. B").Problem, StringComparison.Ordinal);
        Assert.Contains("no line has that number", RoadmapTextCodec.Parse("title: P\n1. A | needs: 9").Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pipe_in_an_items_own_words_is_not_a_field()
    {
        var (content, problem) = RoadmapTextCodec.Parse("title: P\n1. Pipe the output | to a file | for: billing@host");

        Assert.Null(problem);
        Assert.Equal("Pipe the output | to a file", content!.Items[0].Text);
        Assert.Equal(Billing, content.Items[0].IntendedAgent);
    }

    [Theory]
    [InlineData("Drop the export | item: 1")]
    [InlineData("Ship it | for: billing@host")]
    [InlineData("Ship it | needs: 1")]
    [InlineData("Already escaped \\| item: 2")]
    public void An_items_own_words_written_out_never_read_back_as_a_field(string words)
    {
        // A proposal's words are an agent's. The broker refuses a proposal that claims item numbers, and that must not
        // be undone by writing the words where the person reads and confirms them: `| item: 1` in an agent's text would
        // take over item 1's conversation and open work. Must fail against writing the words as they are.
        var proposed = new RoadmapContent("Plan", [new RoadmapItemDraft(null, words, null, []), new RoadmapItemDraft(null, "Other", null, [])]);

        var (back, problem) = RoadmapTextCodec.Parse(RoadmapTextCodec.Write(proposed));

        Assert.Null(problem);
        Assert.Equal(words, back!.Items[0].Text);
        Assert.Null(back.Items[0].Number);
        Assert.Null(back.Items[0].IntendedAgent);
        Assert.Empty(back.Items[0].Needs);
    }

    [Fact]
    public void A_title_that_breaks_a_line_or_a_blank_item_is_reported_by_the_rules()
    {
        Assert.NotNull(RoadmapTextCodec.Parse("title: P\n1.").Problem);
        Assert.NotNull(RoadmapTextCodec.Parse("title:\n1. A").Problem);
    }

    [Fact]
    public void Windows_line_endings_and_blank_lines_are_read_like_any_others()
    {
        var (content, problem) = RoadmapTextCodec.Parse("title: P\r\n\r\n1. A\r\n\r\n2. B | needs: 1\r\n");

        Assert.Null(problem);
        Assert.Equal(2, content!.Items.Count);
    }

    [Fact]
    public void Another_surfaces_text_is_accepted_by_this_one_because_there_is_one_format()
    {
        // The terminal, the web text area and the command line's file all read and write these lines.
        var draft = RoadmapDraft.From(Plan());
        draft.Move(2, -1);

        var (content, _) = RoadmapTextCodec.Parse(RoadmapTextCodec.Write(draft.ToContent()));

        Assert.Equal(draft.ToContent().Items.Select(i => i.Text), content!.Items.Select(i => i.Text));
    }
}
