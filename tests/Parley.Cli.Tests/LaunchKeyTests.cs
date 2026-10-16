using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// The grammar of a launch configuration's server key.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key is not decoration.</b> A client namespaces an MCP server's tools by it, so it becomes a
/// component of every tool name the agent sees, and it is the only thing a withdrawal can use to decide
/// which project's entry it is about to delete. Both of those make it worth a type and a suite rather
/// than string concatenation at four call sites.
/// </para>
/// <para>
/// The properties below are the ones the rest of the feature is allowed to rely on: that two projects
/// cannot produce one key, that a derived key is never the legacy one, and that a key this tooling does
/// not own is recognised as somebody else's rather than merged into.
/// </para>
/// </remarks>
public sealed class LaunchKeyTests
{
    [Fact]
    public void A_named_project_gets_its_own_key()
    {
        Assert.Equal("parley-alpha", LaunchKey.For("alpha"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_project_keeps_the_key_every_repository_already_has(string? project)
    {
        // SPEC.md 4.1 keeps a deployment that names no projects supported, so there is nothing to
        // derive a key from and the legacy spelling is the correct one rather than a leftover.
        Assert.Equal("parley", LaunchKey.For(project));
    }

    [Fact]
    public void A_project_called_parley_collides_with_nothing()
    {
        Assert.Equal("parley-parley", LaunchKey.For("parley"));
        Assert.NotEqual(LaunchKey.For(null), LaunchKey.For("parley"));
    }

    [Fact]
    public void Two_projects_cannot_produce_one_key()
    {
        // Injectivity is what lets a reader take a project back out of a key without ambiguity, which
        // is what the withdrawal path does before it deletes anything.
        string[] projects = ["a", "alpha", "alpha-two", "b", "parley", "x9"];
        var keys = projects.Select(LaunchKey.For).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("parley")]
    [InlineData("parley-alpha")]
    [InlineData("parley-parley")]
    [InlineData("parley-a")]
    public void Ours_is_the_legacy_key_and_anything_a_project_name_can_build(string key)
    {
        Assert.True(LaunchKey.IsOurs(key));
    }

    [Theory]
    [InlineData("parleyx")]          // no separator; a different server that starts with our name
    [InlineData("parley-")]          // a separator and no project
    [InlineData("parley-Alpha")]     // a project name is lower case
    [InlineData("parley-a_b")]       // underscores are not in the grammar
    [InlineData("parley-9a")]        // a project name starts with a letter
    [InlineData("Parley")]           // the key itself is compared exactly
    [InlineData("")]
    public void Anything_a_project_name_cannot_build_is_somebody_elses(string key)
    {
        // Refusing these is what stops an enrolment merging into a server this tooling did not write.
        // Three silent replacements of somebody's MCP configuration are recorded in this codebase's
        // comments; each was a cast rather than malice.
        Assert.False(LaunchKey.IsOurs(key));
    }

    [Fact]
    public void A_trailing_hyphen_is_a_project_name_and_so_it_is_a_key()
    {
        // Written down because it surprised the author, and because the surprise is the argument for
        // asking ProjectName rather than restating its pattern here. `^[a-z][a-z0-9-]{0,31}$` admits a
        // name ending in a hyphen, so `parley-alpha-` is this tooling's key and refusing it would have
        // made a repository in a project called `alpha-` unwithdrawable — a key nothing owned.
        Assert.True(LaunchKey.IsOurs("parley-alpha-"));
        Assert.Equal("alpha-", LaunchKey.ProjectOf("parley-alpha-"));
    }

    [Fact]
    public void The_legacy_key_names_no_project()
    {
        // Null and "a project called nothing" are different answers: the first is an enrolment that
        // names no project.
        Assert.Null(LaunchKey.ProjectOf("parley"));
    }

    [Theory]
    [InlineData("parley-alpha", "alpha")]
    [InlineData("parley-parley", "parley")]
    [InlineData("parley-a", "a")]
    public void A_derived_key_gives_its_project_back(string key, string project)
    {
        Assert.Equal(project, LaunchKey.ProjectOf(key));
    }

    [Theory]
    [InlineData("parleyx")]
    [InlineData("some-other-server")]
    [InlineData("parley-Alpha")]
    public void A_key_that_is_not_ours_has_no_project_to_give(string key)
    {
        Assert.Null(LaunchKey.ProjectOf(key));
    }

    [Fact]
    public void A_project_name_the_grammar_refuses_is_refused_here_too()
    {
        // One grammar, not two. ProjectName is the authority and this asks it rather than repeating
        // its pattern, so a change there cannot leave a key builder behind agreeing with nobody.
        Assert.Throws<ArgumentException>(() => LaunchKey.For("Alpha"));
    }
}
