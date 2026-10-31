using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// What a shared file's name accepts, what it refuses, and the spelling a placement is renamed to (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// Every rule here is also held by the schema, and the store's constraint tests probe that side. These exist
/// because this is the side that tells an agent what was wrong, and a refusal that names the wrong rule sends
/// it to fix the wrong thing.
/// </para>
/// <para>
/// The extension cases matter most. Only <c>.md</c> and <c>.txt</c> are accepted so that nothing placed can run
/// a program when a person opens it, and the refusal for anything else carries the way to place it anyway.
/// </para>
/// <para>
/// Control characters and surrogates are built at run time rather than written into the source, where a raw one
/// is invisible to a reader and to a diff.
/// </para>
/// </remarks>
[Unit]
public sealed class SharedFileNameTests
{
    [Theory]
    [InlineData("price-snapshot.md")]
    [InlineData("PriceSnapshot.cs.txt")]
    [InlineData("NOTES.MD")]
    [InlineData("a.md")]
    [InlineData("log_2026-09-14.txt")]
    [InlineData("con-notes.md")]
    [InlineData("console.txt")]
    public void A_well_formed_name_is_accepted(string candidate)
    {
        Assert.Equal(SharedFileNameProblem.None, SharedFileName.Check(candidate));
        Assert.Equal(candidate, SharedFileName.Create(candidate).Value);
        Assert.Equal(candidate, SharedFileName.Safe(candidate));
    }

    [Theory]
    [InlineData("PriceSnapshot.cs")]
    [InlineData("run.bat")]
    [InlineData("tool.exe")]
    [InlineData("notes")]
    [InlineData("notes.md.exe")]
    [InlineData("archive.zip")]
    public void Any_other_extension_is_refused_with_the_way_to_place_it(string candidate)
    {
        Assert.Equal(SharedFileNameProblem.ExtensionNotAllowed, SharedFileName.Check(candidate));

        var refusal = Assert.Throws<ArgumentException>(() => SharedFileName.Create(candidate));

        Assert.Contains(".txt", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("PriceSnapshot.cs.txt", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("docs/notes.md")]
    [InlineData("docs\\notes.md")]
    [InlineData("c:notes.md")]
    [InlineData("what?.md")]
    [InlineData("a*b.md")]
    [InlineData("say \"hi\".md")]
    [InlineData("a<b>.md")]
    [InlineData("a|b.md")]
    [InlineData(" notes.md")]
    [InlineData("log 2026-09-14.txt")]
    [InlineData("Übersicht.md")]
    [InlineData("a+b.md")]
    [InlineData("a~b.md")]
    public void A_name_with_a_character_outside_the_set_is_refused_as_it_is(string candidate)
    {
        Assert.Equal(SharedFileNameProblem.DisallowedCharacter, SharedFileName.Check(candidate));
        Assert.False(SharedFileName.TryCreate(candidate, out _));
    }

    [Theory]
    [InlineData(".hidden.md", SharedFileNameProblem.LeadingDot)]
    [InlineData(".md", SharedFileNameProblem.LeadingDot)]
    [InlineData("", SharedFileNameProblem.Empty)]
    [InlineData(null, SharedFileNameProblem.Empty)]
    public void A_name_that_breaks_a_rule_is_refused_for_that_rule(string? candidate, SharedFileNameProblem problem)
    {
        Assert.Equal(problem, SharedFileName.Check(candidate));
        Assert.False(SharedFileName.TryCreate(candidate, out _));
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x1F)]
    [InlineData(0x7F)]
    public void A_control_character_is_outside_the_set_and_renamed_like_any_other(int code)
    {
        var candidate = "notes" + (char)code + ".md";

        Assert.Equal(SharedFileNameProblem.DisallowedCharacter, SharedFileName.Check(candidate));
        Assert.Equal("notes_.md", SharedFileName.Safe(candidate));
    }

    [Theory]
    [InlineData("Meeting notes v2.md", "Meeting_notes_v2.md")]
    [InlineData("a:b.md", "a_b.md")]
    [InlineData("a?b.md", "a_b.md")]
    [InlineData("docs/notes.md", "docs_notes.md")]
    [InlineData("docs\\notes.md", "docs_notes.md")]
    [InlineData(" notes.md", "_notes.md")]
    [InlineData("Übersicht.md", "_bersicht.md")]
    [InlineData("price  list.md", "price__list.md")]
    public void A_placement_is_renamed_one_character_for_one(string asked, string stored)
    {
        Assert.Equal(stored, SharedFileName.Safe(asked));
        Assert.Equal(SharedFileNameProblem.None, SharedFileName.Check(stored));
    }

    [Fact]
    public void A_character_outside_the_basic_plane_becomes_one_underscore_not_two()
    {
        // Two UTF-16 units and one character to SQLite. Two underscores would make the stored name longer than the
        // name asked for, as SQLite measures both, and a name that fit could stop fitting by being made safe.
        var emoji = char.ConvertFromUtf32(0x1F600);

        Assert.Equal("a_b.md", SharedFileName.Safe("a" + emoji + "b.md"));
    }

    [Fact]
    public void A_lone_surrogate_becomes_an_underscore_rather_than_being_stored_as_something_else()
    {
        Assert.Equal("a_b.md", SharedFileName.Safe("a" + (char)0xD800 + "b.md"));
        Assert.Equal("a_b.md", SharedFileName.Safe("a" + (char)0xDC00 + "b.md"));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData(".. notes.md")]
    public void Making_a_name_safe_does_not_make_a_leading_dot_acceptable(string asked)
    {
        // '../escape.txt' is '.._escape.txt' once made safe: flat, but still a hidden file on two platforms.
        Assert.Equal(SharedFileNameProblem.LeadingDot, SharedFileName.Check(SharedFileName.Safe(asked)));
    }

    [Theory]
    [InlineData("con.md")]
    [InlineData("CON.txt")]
    [InlineData("nul.md")]
    [InlineData("Aux.txt")]
    [InlineData("com1.md")]
    [InlineData("LPT9.txt")]
    [InlineData("con.notes.md")]
    public void A_reserved_device_name_is_refused_with_or_without_more_extension(string candidate)
    {
        Assert.Equal(SharedFileNameProblem.ReservedDeviceName, SharedFileName.Check(candidate));
    }

    [Fact]
    public void Names_differing_only_in_case_are_one_name()
    {
        Assert.Equal(SharedFileName.Create("Readme.md"), SharedFileName.Create("README.md"));
    }

    [Fact]
    public void The_key_is_what_the_schema_computes_from_the_name()
    {
        // The schema checks name_key = upper(name), and SQLite's upper() folds ASCII only — which is every letter a
        // name can hold now.
        Assert.Equal("MIXED-CASE.MD", SharedFileName.Create("mixed-Case.md").Key);
    }

    [Fact]
    public void A_name_of_exactly_the_limit_is_accepted_and_one_past_it_is_refused()
    {
        var atLimit = new string('a', SharedFileName.MaxNameCharacters - 3) + ".md";
        var pastLimit = new string('a', SharedFileName.MaxNameCharacters - 2) + ".md";

        Assert.Equal(SharedFileNameProblem.None, SharedFileName.Check(atLimit));
        Assert.Equal(SharedFileNameProblem.TooLong, SharedFileName.Check(pastLimit));
    }

    [Fact]
    public void A_path_inside_the_location_is_bounded_with_the_folder_counted_and_the_location_not()
    {
        var folder = new string('f', 100);
        var fits = SharedFileName.Create(new string('n', 154 - 3) + ".md");
        var doesNot = SharedFileName.Create(new string('n', 155 - 3) + ".md");

        Assert.Equal(154, SharedFileName.CharactersLeftIn(folder));
        Assert.True(fits.FitsIn(folder));
        Assert.False(doesNot.FitsIn(folder));
    }

    [Fact]
    public void A_lookup_by_a_name_outside_the_set_is_told_the_spelling_a_placement_would_have_had()
    {
        var said = SharedFileName.DescribeLookup("Meeting notes.md");

        Assert.Contains("'Meeting_notes.md'", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lookup_whose_safe_spelling_is_still_no_name_is_not_offered_one()
    {
        Assert.DoesNotContain("stored as", SharedFileName.DescribeLookup("run me.exe"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_collision_sentence_names_all_three_and_makes_control_characters_visible()
    {
        var said = SharedFileName.SafeNameTaken("a\nb.md", "a_b.md", "A_B.md");

        Assert.Contains("'a?b.md'", said, StringComparison.Ordinal);
        Assert.Contains("will be named 'a_b.md'", said, StringComparison.Ordinal);
        Assert.Contains("('A_B.md')", said, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_not_listed_sentence_says_nothing_placed_them_and_how_a_file_is_shared()
    {
        // Files enter the location only by placement (SPEC.md §9.5), so a file in a folder that nothing placed is counted
        // whatever its name, and the person is told how to share one.
        var said = SharedFileListing.NotListedSentenceFor(1)!;

        Assert.Contains("nothing placed them", said, StringComparison.Ordinal);
        Assert.Contains("Place a file through Parley", said, StringComparison.Ordinal);
        Assert.Null(SharedFileListing.NotListedSentenceFor(0));
    }

    [Fact]
    public void Every_problem_is_described_in_words()
    {
        foreach (var problem in Enum.GetValues<SharedFileNameProblem>())
        {
            Assert.False(string.IsNullOrWhiteSpace(SharedFileName.Describe(problem)));
        }
    }
}
