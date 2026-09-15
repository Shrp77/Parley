using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// What a shared file's name accepts, and what it refuses (SPEC.md §9.5).
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
/// Control characters are built at run time rather than written into the source, where a raw one is invisible
/// to a reader and to a diff.
/// </para>
/// </remarks>
public sealed class SharedFileNameTests
{
    private const string UmlautComposed = "Übersicht.md";
    private const string UmlautLower = "übersicht.md";

    private static readonly string UmlautDecomposed = "U" + (char)0x0308 + "bersicht.md";

    [Theory]
    [InlineData("price-snapshot.md")]
    [InlineData("PriceSnapshot.cs.txt")]
    [InlineData("NOTES.MD")]
    [InlineData("a.md")]
    [InlineData(UmlautComposed)]
    [InlineData("log 2026-09-14.txt")]
    [InlineData("con-notes.md")]
    [InlineData("console.txt")]
    public void A_well_formed_name_is_accepted(string candidate)
    {
        Assert.Equal(SharedFileNameProblem.None, SharedFileName.Check(candidate));
        Assert.Equal(candidate, SharedFileName.Create(candidate).Value);
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
    [InlineData("../escape.txt", SharedFileNameProblem.PathSeparator)]
    [InlineData("docs/notes.md", SharedFileNameProblem.PathSeparator)]
    [InlineData("docs\\notes.md", SharedFileNameProblem.PathSeparator)]
    [InlineData("c:notes.md", SharedFileNameProblem.ForbiddenCharacter)]
    [InlineData("what?.md", SharedFileNameProblem.ForbiddenCharacter)]
    [InlineData("a*b.md", SharedFileNameProblem.ForbiddenCharacter)]
    [InlineData("say \"hi\".md", SharedFileNameProblem.ForbiddenCharacter)]
    [InlineData("a<b>.md", SharedFileNameProblem.ForbiddenCharacter)]
    [InlineData("a|b.md", SharedFileNameProblem.ForbiddenCharacter)]
    [InlineData(".hidden.md", SharedFileNameProblem.LeadingDotOrSpace)]
    [InlineData(".md", SharedFileNameProblem.LeadingDotOrSpace)]
    [InlineData(" notes.md", SharedFileNameProblem.LeadingDotOrSpace)]
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
    public void A_name_with_a_control_character_is_refused(int code)
    {
        Assert.Equal(SharedFileNameProblem.ControlCharacter, SharedFileName.Check("notes" + (char)code + ".md"));
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
    public void A_lone_surrogate_is_refused_rather_than_stored_as_something_else()
    {
        // SQLite stores UTF-8, and a lone surrogate has no UTF-8 spelling, so whatever reached the store would
        // be a different name from the one the agent was told it placed.
        Assert.Equal(SharedFileNameProblem.ForbiddenCharacter, SharedFileName.Check("a" + (char)0xD800 + "b.md"));
    }

    [Fact]
    public void Names_differing_only_in_case_are_one_name_for_every_letter()
    {
        Assert.Equal(SharedFileName.Create("Readme.md"), SharedFileName.Create("README.md"));
        Assert.Equal(SharedFileName.Create(UmlautComposed), SharedFileName.Create(UmlautLower));
    }

    [Fact]
    public void A_name_written_decomposed_is_a_different_name_from_one_written_composed()
    {
        // Pinned rather than hoped about. The first key normalised to form C and this test asserted the two
        // spellings were one name; it failed, because the build's InvariantGlobalization leaves non-ASCII text
        // unnormalised. SPEC.md §9.5 now says the key folds case only, and this holds the code to that sentence
        // so a later change to normalisation is a decision somebody makes rather than a surprise.
        Assert.NotEqual(UmlautComposed, UmlautDecomposed);
        Assert.NotEqual(SharedFileName.Create(UmlautComposed).Key, SharedFileName.Create(UmlautDecomposed).Key);
    }

    [Fact]
    public void The_key_holds_the_shape_the_schema_checks()
    {
        // The schema checks name_key = upper(name_key), and SQLite's upper() folds ASCII only. A key that
        // left an ASCII letter lower case would be refused by storage.
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
    public void Length_is_counted_as_the_store_counts_it_not_in_utf16_units()
    {
        // An emoji is two UTF-16 units and one character to SQLite. Counting units would refuse a name the
        // store accepts; counting characters matches the CHECK exactly.
        var emoji = char.ConvertFromUtf32(0x1F600);
        var name = string.Concat(Enumerable.Repeat(emoji, SharedFileName.MaxNameCharacters - 3)) + ".md";

        Assert.Equal(SharedFileName.MaxNameCharacters, SharedFileName.CharacterCount(name));
        Assert.Equal(SharedFileNameProblem.None, SharedFileName.Check(name));
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
    public void Every_problem_is_described_in_words()
    {
        foreach (var problem in Enum.GetValues<SharedFileNameProblem>())
        {
            Assert.False(string.IsNullOrWhiteSpace(SharedFileName.Describe(problem)));
        }
    }
}
