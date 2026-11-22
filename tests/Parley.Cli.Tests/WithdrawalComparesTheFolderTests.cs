using System.Diagnostics;
using System.Text.Json.Nodes;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a withdrawal takes the catalogue line of the repository it withdraws however that line spells the folder, and only
/// then (Enrolment.SameFolder), and that a repository whose file has lost its identity is still withdrawn for its agent.
/// </summary>
/// <remarks>
/// <c>EnrolmentTests.A_catalogue_line_naming_another_repository_is_never_taken_by_a_withdrawal</c> holds the refusal. Nothing
/// held the other half: with the comparison made by text alone, or case-sensitive on Windows, every test still passed, and a
/// withdrawal whose catalogue line spells the folder another way (a drive letter in lower case, a junction) left the line
/// behind while telling the person it was another repository's, so the node could still start the checkout.
/// </remarks>
[Integration]
public sealed class WithdrawalComparesTheFolderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-withdrawal-folder-" + Path.GetRandomFileName());

    public WithdrawalComparesTheFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Alias))
            {
                Directory.Delete(Alias);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private string Workspace => Path.Combine(_root, "orders");

    private string Alias => Path.Combine(_root, "orders-alias");

    private string CatalogueFile => Path.Combine(_root, "node.json");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private async Task<string> AddAsync()
    {
        await Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, "orders", null, Unattended: true) { Enroll = true },
            "acme",
            CatalogueFile,
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

        var launch = JsonNode.Parse(await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!;

        return launch[LaunchConfiguration.IdentityMember]!.GetValue<string>();
    }

    private async Task SpellCatalogueLineAsync(string identity, string workspace)
    {
        var catalogue = JsonNode.Parse(await File.ReadAllTextAsync(CatalogueFile, TestContext.Current.CancellationToken))!;
        catalogue["agents"]![identity]!["workspace"] = workspace;
        await File.WriteAllTextAsync(CatalogueFile, catalogue.ToJsonString(), TestContext.Current.CancellationToken);
    }

    private async Task<bool> ListedAsync(string identity) =>
        ((JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(CatalogueFile, TestContext.Current.CancellationToken))!["agents"]!)
            .ContainsKey(identity);

    [Fact]
    public async Task A_catalogue_line_spelling_the_folder_in_another_case_is_taken_where_names_ignore_case()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "Folder names ignore case only here.");

        var identity = await AddAsync();
        await SpellCatalogueLineAsync(identity, Workspace.ToUpperInvariant());

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", CatalogueFile, TestContext.Current.CancellationToken);

        Assert.False(await ListedAsync(identity), "the line was left: " + string.Join(" | ", outcome.Notes));
        Assert.DoesNotContain(outcome.Notes, note => note.Contains("another repository's", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_catalogue_line_reaching_the_folder_through_a_junction_is_taken()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A junction is a Windows link.");

        var identity = await AddAsync();

        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Alias}\" \"{Workspace}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!)
        {
            await mklink.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, mklink.ExitCode);
        }

        await SpellCatalogueLineAsync(identity, Alias);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", CatalogueFile, TestContext.Current.CancellationToken);

        Assert.False(await ListedAsync(identity), "the line was left: " + string.Join(" | ", outcome.Notes));
    }

    [Fact]
    public async Task A_launch_configuration_another_program_holds_is_never_called_absent()
    {
        // Held whole by another program for longer than the withdrawal waits: the file is there, so the person must not be
        // told it is already gone (Enrolment.LocallyThere answers "there" for anything but nothing).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses a second open of a file held with no sharing.");

        await AddAsync();

        Enrolment.Outcome? outcome = null;

        using (new FileStream(LaunchFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                outcome = await Enrolment.WithdrawAsync(
                    Workspace, "orders", "acme", CatalogueFile, TestContext.Current.CancellationToken);
            }
            catch (IOException)
            {
                // Saying it could not be opened is an honest answer.
            }
        }

        Assert.True(
            outcome is null || !outcome.Notes.Any(note => note.Contains("already absent", StringComparison.Ordinal)),
            "a held file was reported absent");
        Assert.Contains("parley-acme", await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_launch_configuration_held_for_a_moment_is_waited_for()
    {
        // Another program (an editor saving) holds the file for a moment: the withdrawal waits for it rather than failing.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses a second open of a file held with no sharing.");

        await AddAsync();

        var holder = new FileStream(LaunchFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var released = Task.Run(async () =>
        {
            await Task.Delay(400, TestContext.Current.CancellationToken);
            await holder.DisposeAsync();
        }, TestContext.Current.CancellationToken);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", CatalogueFile, TestContext.Current.CancellationToken);

        await released;

        Assert.Contains(LaunchFile, outcome.Written);
        Assert.DoesNotContain("parley-acme", await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_whose_file_lost_its_identity_is_still_withdrawn_for_its_own_agent()
    {
        // The registry knows the agent by an identity; the file no longer says one. Nothing says it is another agent's, so its
        // entry goes, as it did before identities were compared.
        var identity = await AddAsync();
        var launch = JsonNode.Parse(await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!.AsObject();

        Assert.True(launch.Remove(LaunchConfiguration.IdentityMember));
        await File.WriteAllTextAsync(LaunchFile, launch.ToJsonString(), TestContext.Current.CancellationToken);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders@test", "acme", CatalogueFile, AgentIdentity.Read(identity), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(outcome.Notes, note => note.Contains("another agent's", StringComparison.Ordinal));
        Assert.DoesNotContain(
            "parley-acme", await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
