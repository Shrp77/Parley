using Parley.Configuration;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That Parley keeps its work only on this machine's own disks: never a synced cloud folder, a share or a virtual drive.
/// </summary>
/// <remarks>
/// <b>The owner's rule, 2026-10-29.</b> A repository, a deployment's root and an agent's workspace are refused on
/// OneDrive, Google Drive, Dropbox, iCloud Drive, Box, a network share or any other drive that is not this machine's
/// own. Each sync client is found where it says it keeps its folder, so the environment is a seam here: no test reads
/// the developer's own.
/// </remarks>
public sealed class OnlyLocalStorageTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "parley-home-" + Guid.NewGuid().ToString("N"));

    public OnlyLocalStorageTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    private Func<string, string?> Environment(params (string Name, string Value)[] set) =>
        name => name is "USERPROFILE" or "HOME"
            ? _home
            : set.FirstOrDefault(pair => string.Equals(pair.Name, name, StringComparison.Ordinal)).Value;

    [Theory]
    [InlineData("Dropbox", "Dropbox")]
    [InlineData("iCloudDrive", "iCloud Drive")]
    [InlineData("Box", "Box")]
    [InlineData("Google Drive", "Google Drive")]
    [InlineData("My Drive", "Google Drive")]
    [InlineData("OneDrive", "OneDrive")]
    public void A_folder_a_sync_client_keeps_in_the_home_is_refused(string folder, string named)
    {
        var repository = Path.Combine(_home, folder, "src", "orders");

        Assert.Contains($"on {named}", LocalStorage.Problem(repository, Environment()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_folder_onedrive_names_for_itself_is_refused_wherever_it_is()
    {
        var onedrive = Path.Combine(_home, "Somewhere", "OneDrive - Contoso");
        var repository = Path.Combine(onedrive, "orders");

        Assert.Contains("on OneDrive", LocalStorage.Problem(repository, Environment(("OneDriveCommercial", onedrive))), StringComparison.Ordinal);
    }

    [Fact]
    public void The_folder_dropbox_records_in_its_info_file_is_refused()
    {
        var local = Path.Combine(_home, "AppData", "Local");
        var dropbox = Path.Combine(_home, "Elsewhere", "Team Dropbox");
        Directory.CreateDirectory(Path.Combine(local, "Dropbox"));
        File.WriteAllText(
            Path.Combine(local, "Dropbox", "info.json"),
            System.Text.Json.JsonSerializer.Serialize(new { business = new { path = dropbox } }));

        Assert.Contains("on Dropbox", LocalStorage.Problem(Path.Combine(dropbox, "orders"), Environment(("LOCALAPPDATA", local))), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Library/CloudStorage/GoogleDrive-someone/My Drive")]
    [InlineData("Library/Mobile Documents/com~apple~CloudDocs")]
    public void The_folders_macos_keeps_for_cloud_providers_are_refused(string folder)
    {
        var repository = Path.Combine([_home, .. folder.Split('/'), "orders"]);

        Assert.NotNull(LocalStorage.Problem(repository, Environment()));
    }

    [Fact]
    public void A_share_named_as_a_path_is_refused_before_anything_is_asked()
    {
        Assert.Contains("another machine", LocalStorage.Problem(@"\\192.0.2.9\share\orders", Environment()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_on_this_machines_own_disk_is_local()
    {
        var repository = Path.Combine(_home, "src", "orders");
        Directory.CreateDirectory(repository);

        Assert.Null(LocalStorage.Problem(repository, Environment()));
        Assert.Null(LocalStorage.Problem(Path.Combine(_home, "Boxes", "orders"), Environment()));
    }

    [Fact]
    public void Enrolment_refuses_a_repository_that_is_not_on_local_storage()
    {
        var problems = EnrollmentPlan.Validate(
            [new EnrollmentEntry(@"\\192.0.2.9\share\orders", "orders", "Carts and order totals.", false)],
            _ => true);

        Assert.Contains(problems, problem => problem.Contains("only on this machine's own disks", StringComparison.Ordinal));
    }
}
