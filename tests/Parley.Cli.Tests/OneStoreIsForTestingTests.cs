using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That nothing the tools print tells a person to deploy with the one-store options, which are for testing only.
/// </summary>
/// <remarks>
/// <para>
/// A machine is deployed as projects: `parley project add` names each one's store and endpoint, and the logon entry
/// serves them all. `serve --db`, `autostart install --db` and <c>PARLEY_DB</c> serve one store outside the manifest,
/// so a test rig need name no project — the owner's call, recorded in the README, the help and the skill.
/// </para>
/// <para>
/// <b>Found by the gate over the installer change, in the two places a person is most likely to read it:</b> the
/// refusal `parley serve` gives with nothing named, and the next steps `parley enroll apply` prints. Both offered only
/// the one-store route. Read off the source, because the sentence is what matters and the paths that print it need a
/// broker and a set of repositories to reach.
/// </para>
/// </remarks>
public sealed class OneStoreIsForTestingTests
{
    public static TheoryData<string> Printing => ["src/Parley.Setup/EnrollCommand.cs", "src/Parley.Cli/ServeCommand.cs", "src/Parley.Cli/AutostartCommand.cs"];

    [Theory]
    [MemberData(nameof(Printing))]
    public void No_printed_step_deploys_with_one_store(string source)
    {
        var text = RepositoryFile.Read(source);

        Assert.DoesNotContain("parley serve --db <path to the store>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("autostart install --db <path to the store>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Enrolment_names_the_project_and_the_entry_that_serves_it()
    {
        var text = RepositoryFile.Read("src/Parley.Setup/EnrollCommand.cs");

        Assert.Contains("parley project add <name> --db <path to the store> --listen <url>", text, StringComparison.Ordinal);
        Assert.Contains("parley autostart install", text, StringComparison.Ordinal);
    }
}
