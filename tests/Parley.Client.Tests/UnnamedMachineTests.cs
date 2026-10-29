using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That a machine which never named its deployments still has a console.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §4.1 is explicit that running without a manifest — configured entirely by explicit
/// paths — is supported, and it is how the deployment this was built for actually runs. Every
/// other operator command works there, because each takes <c>--broker</c>, reads
/// <c>PARLEY_BROKER</c>, or falls back to the local one.
/// </para>
/// <para>
/// A console that showed an empty page on such a machine would be the only client unable to reach
/// a broker the rest of the tool talks to happily — and it would say "no projects are named on
/// this machine", which is true, reads as "nothing to see", and is the first thing somebody sees
/// after installing.
/// </para>
/// <para>
/// The fallback does not weaken "pick a project first". The list still renders, still has to be
/// chosen from, and still scopes everything below it. What changes is where the one entry came
/// from.
/// </para>
/// </remarks>
public sealed class UnnamedMachineTests
{
    /// <summary>A root this machine has never created.</summary>
    /// <remarks>
    /// **It must not end in `projects.json`.** The variable and the flag name a root, and a value
    /// whose final segment is the manifest file name is *refused* — so a fixture spelled that way
    /// describes a misconfigured machine rather than an unconfigured one, and every test
    /// here would fail for a reason that had nothing to do with fallbacks.
    /// </remarks>
    private static string NoManifestHere =>
        Path.Combine(Path.GetTempPath(), $"parley-absent-{Guid.NewGuid():N}");

    [Fact]
    public void A_machine_with_no_manifest_offers_the_broker_its_commands_use()
    {
        using var directory = BrokerDirectory.FromManifest(
            NoManifestHere, new FallbackBroker("http://127.0.0.1:51707"));

        var offered = Assert.Single(directory.Projects);

        Assert.Equal("http://127.0.0.1:51707", offered.Endpoint);
        Assert.Null(directory.ManifestProblem);
    }

    [Fact]
    public void A_bare_host_and_port_is_offered_as_every_other_tool_reads_it()
    {
        // Found by review, lens 1 run 60: the docs tell a person to run `parley --broker 127.0.0.1:19100`, the CLI adds
        // the scheme, and the surfaces dialled the text as typed, so the one row they offered could never answer.
        using var directory = BrokerDirectory.FromManifest(NoManifestHere, new FallbackBroker("127.0.0.1:19100"));

        Assert.Equal("http://127.0.0.1:19100", Assert.Single(directory.Projects).Endpoint);
    }

    [Fact]
    public void The_offered_project_is_reachable_by_the_name_the_page_links_to()
    {
        // The list renders a link built from the name, and every page below reads the name back
        // out of the query string. A name the directory cannot then find would give a project
        // list with one entry and a link that redirects straight back to it.
        using var directory = BrokerDirectory.FromManifest(
            NoManifestHere, new FallbackBroker("http://127.0.0.1:51707"));

        var offered = directory.Projects[0];

        Assert.NotNull(directory.Find(offered.Name.Value));
    }

    [Fact]
    public void It_says_it_was_not_named_rather_than_pretending_to_be_configured()
    {
        // The operator should be able to tell this row from one they added. It is the difference
        // between a deployment they set up and a default the console chose for them, and the
        // second is worth one sentence saying how to make it the first.
        using var directory = BrokerDirectory.FromManifest(
            NoManifestHere, new FallbackBroker("http://127.0.0.1:51707"));

        var offered = directory.Projects[0];

        Assert.Contains("A broker reached by address", offered.Description, StringComparison.Ordinal);
        Assert.Contains("no project of its own", offered.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void No_fallback_is_offered_when_none_was_given()
    {
        // `dotnet run` in the console's own folder passes none. An empty list is right there:
        // nothing has told it where a broker is, and inventing the default would be a guess
        // rendered as a fact.
        using var directory = BrokerDirectory.FromManifest(NoManifestHere);

        Assert.Empty(directory.Projects);
    }

    [Fact]
    public void An_endpoint_cannot_be_passed_where_a_manifest_path_belongs()
    {
        // It was a plain string beside the path, and a swapped call read the address as a path
        // that did not exist -- an ordinary machine with nothing configured, so an empty list and
        // no error. This records that the fallback now has a type of its own, which turns that
        // mistake into a compiler error rather than a page that quietly shows nothing.
        var fallback = typeof(BrokerDirectory)
            .GetMethod(nameof(BrokerDirectory.FromManifest))!
            .GetParameters()[1];

        Assert.Equal(typeof(FallbackBroker), fallback.ParameterType);
    }

    [Fact]
    public void A_machine_that_named_its_projects_gets_no_extra_row()
    {
        // The rule that keeps this from being an inventory of guesses. A machine that has named
        // its deployments has said what it wants listed, and quietly adding an unnamed row would
        // be the console showing a project the operator did not configure.
        var root = Path.Combine(
            Path.GetTempPath(), $"parley-named-{Guid.NewGuid():N}");

        Directory.CreateDirectory(root);

        // Written the way `parley project add` writes it rather than by hand. A hand-rolled file that the
        // reader rejects would make this pass for the wrong reason: no projects and no fallback row looks
        // exactly like the fallback correctly declining to appear.
        Parley.Configuration.ProjectManifest.Write(
            root,
            [new ProjectSettings(
                ProjectName.Create("alpha"),
                Parley.Configuration.ProjectFolder.StoreFor(root, ProjectName.Create("alpha")),
                "http://127.0.0.1:5301",
                null)]);

        try
        {
            using var directory = BrokerDirectory.FromManifest(root, new FallbackBroker("http://127.0.0.1:51707"));

            Assert.Null(directory.ManifestProblem);

            var only = Assert.Single(directory.Projects);

            Assert.Equal("alpha", only.Name.Value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
