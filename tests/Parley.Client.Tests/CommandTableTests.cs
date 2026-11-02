using Parley.Client.Seating;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// What a leading slash means, and what a command declares.
/// </summary>
/// <remarks>
/// <para>
/// Two rules decide the whole of it, and together they decide <c>/etc/hosts</c> without reference to what any
/// command is called: a command is <c>/</c> and an identifier containing no second <c>/</c>, and an identifier
/// that is not a command is text.
/// </para>
/// <para>
/// The second rule inverts the failure mode, which is why the near miss exists. It is no longer "my command was
/// refused" but "my command was sent" — so a typo becomes an ask every participant must answer, and what makes
/// that survivable is naming the command Tab would have completed before Enter is pressed.
/// </para>
/// </remarks>
[Unit]
public sealed class CommandTableTests
{
    [Fact]
    public void A_recognised_identifier_is_a_command()
    {
        var resolved = Commands.Resolve("/add @billing");

        Assert.NotNull(resolved.Command);
        Assert.Equal("add", resolved.Command!.Id);
        Assert.Equal("@billing", resolved.Rest);
    }

    [Fact]
    public void An_unrecognised_identifier_is_text_and_is_not_refused()
    {
        // The identifiers somebody might type are not a closed set they can be expected to know.
        var resolved = Commands.Resolve("/nonsense do the thing");

        Assert.Null(resolved.Command);
        Assert.Equal("/nonsense do the thing", resolved.Rest);
        Assert.Null(resolved.NearMiss);
    }

    [Theory]
    [InlineData("/etc/hosts is unreadable")]
    [InlineData("/usr/bin/parley")]
    [InlineData("/a/b")]
    public void A_second_slash_makes_it_text_whatever_any_command_is_called(string line)
    {
        var resolved = Commands.Resolve(line);

        Assert.Null(resolved.Command);
        Assert.Equal(line, resolved.Rest);
    }

    [Fact]
    public void A_line_that_does_not_start_with_a_slash_is_text()
    {
        Assert.Null(Commands.Resolve("what about /etc/hosts?").Command);
    }

    [Fact]
    public void A_near_miss_names_the_command_it_nearly_was()
    {
        // The whole of the mitigation. `/ad` is text, and would be sent as one, so the line has to say that the
        // thing one keystroke away was a command.
        var resolved = Commands.Resolve("/ad");

        Assert.Null(resolved.Command);
        Assert.Equal("add", resolved.NearMiss);
    }

    [Fact]
    public void And_a_near_miss_is_only_reported_while_it_is_still_a_prefix()
    {
        // `/added` is not on the way to anything, so naming `add` would be a suggestion nobody can act on.
        Assert.Null(Commands.Resolve("/added").NearMiss);
    }

    [Fact]
    public void A_bare_slash_is_every_command_and_no_identifier()
    {
        var resolved = Commands.Resolve("/");

        Assert.Null(resolved.Command);

        // No near miss either: everything is a prefix of everything, so naming one would be arbitrary.
        Assert.Null(resolved.NearMiss);
    }

    [Fact]
    public void An_identifier_resolves_whatever_its_case()
    {
        Assert.NotNull(Commands.Resolve("/ADD @billing").Command);
    }

    [Fact]
    public void Add_is_a_draft_command_and_so_cannot_be_sent_alone()
    {
        // §5.0 makes membership derived: an agent joins by being party to an exchange, so adding with nothing to
        // say would have to invent a Request — and every message is answered.
        var add = Assert.Single(Commands.Table, c => c.Id == "add");

        Assert.Equal(Does.Draft, add.Kind);
    }

    [Fact]
    public void Add_takes_one_agent_and_it_is_required()
    {
        var add = Assert.Single(Commands.Table, c => c.Id == "add");
        var takes = Assert.Single(add.Takes);

        Assert.Equal(Takes.Agent, takes.Kind);
        Assert.True(takes.Required);
    }

    [Fact]
    public void A_missing_required_parameter_is_refused_with_what_was_expected()
    {
        var add = Assert.Single(Commands.Table, c => c.Id == "add");

        var why = Commands.Wrong(add, given: 0);

        Assert.NotNull(why);
        Assert.Contains("add", why!, StringComparison.Ordinal);
    }

    [Fact]
    public void More_parameters_than_it_takes_is_refused_too()
    {
        var add = Assert.Single(Commands.Table, c => c.Id == "add");

        Assert.NotNull(Commands.Wrong(add, given: 2));
    }

    [Fact]
    public void And_the_right_number_is_not_refused()
    {
        var add = Assert.Single(Commands.Table, c => c.Id == "add");

        Assert.Null(Commands.Wrong(add, given: 1));
    }

    [Fact]
    public void Every_command_says_what_it_does_and_holds_no_slash_in_its_identifier()
    {
        // The identifier rule is what makes the path case decidable, so it is a property of the table rather
        // than of one entry. And a command with nothing to say cannot be listed usefully.
        Assert.All(Commands.Table, command =>
        {
            Assert.DoesNotContain('/', command.Id);
            Assert.NotEmpty(command.Id);
            Assert.NotEmpty(command.Says);
        });
    }

    [Fact]
    public void No_command_destroys_anything_without_declaring_its_door()
    {
        // Destructive commands are permitted and expected to grow. What they may not do is arrive without the
        // door the operation already has — a description, and the subject's own name typed back. Declared here
        // so the door is part of what the command is rather than something a handler remembers.
        Assert.All(
            Commands.Table.Where(c => c.Kind == Does.Acting),
            command => Assert.True(
                command.Door is null or { Length: > 0 },
                $"/{command.Id} acts and declares an empty door"));
    }

    [Fact]
    public void The_identifiers_offered_to_the_completion_list_are_the_table()
    {
        // One source. A list built from anything else would offer a command that does not exist, or hide one
        // that does.
        Assert.Equal([.. Commands.Table.Select(c => c.Id)], Commands.Ids);
    }
}
