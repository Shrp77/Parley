using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// Windows path forms that lead out of a repository are refused by the file tools, whichever form they take.
/// </summary>
/// <remarks>
/// Written as a probe by the review gate (run 2.2) and kept: a device path, a UNC share, a drive-relative path and an
/// alternate data stream each resolve differently from an ordinary path, and a check written against ordinary paths
/// could pass every other test here and still let one of them through.
/// </remarks>
public sealed class WindowsRepositoryPathTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-gate-paths-" + Guid.NewGuid().ToString("N"));

    public WindowsRepositoryPathTests()
    {
        Directory.CreateDirectory(Billing);
        Directory.CreateDirectory(Orders);
        Directory.CreateDirectory(Outside);
    }

    private string Billing => Path.Combine(_root, "billing");

    private string Orders => Path.Combine(_root, "orders");

    private string Outside => Path.Combine(_root, "outside");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task Windows_path_forms_that_leave_the_repository_are_refused_for_fetching_and_placing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows path forms");
        }

        await using var fixture = new ServerFixture(T0);
        var (_, billing) = await fixture.LaunchInAsync("billing@test", Billing, Ct);
        var (_, orders) = await fixture.LaunchInAsync("orders@test", Orders, Ct);

        await File.WriteAllTextAsync(Path.Combine(Billing, "notes.md"), "net", Ct);
        await billing.FilePlaceAsync("notes.md", conversationId: "pricing", cancellationToken: Ct);

        var drive = Path.GetPathRoot(Outside)!.TrimEnd('\\');
        var outsideTarget = Path.Combine(Outside, "planted.md");
        var admitted = new List<string>();

        foreach (var to in new[]
                 {
                     @"\\?\" + outsideTarget,
                     @"\\localhost\" + drive.TrimEnd(':') + @"$" + outsideTarget[2..],
                     drive + Path.GetRelativePath(Directory.GetCurrentDirectory(), outsideTarget) is var relative && relative.Contains(':') ? relative : drive + "planted.md",
                     @"\\.\" + outsideTarget,
                     Path.Combine("..", "outside", "planted.md"),
                     "notes.md:hidden",
                 })
        {
            var result = await orders.FileFetchAsync("notes.md", to, conversationId: "pricing", replace: true, cancellationToken: Ct);

            if (result["ok"]?.GetValue<bool>() == true)
            {
                admitted.Add($"fetch to '{to}' wrote {result["path"]}");
            }
        }

        foreach (var from in new[] { @"\\?\" + Path.Combine(Outside, "secret.md"), @"\\.\" + Path.Combine(Outside, "secret.md") })
        {
            await File.WriteAllTextAsync(Path.Combine(Outside, "secret.md"), "not billing's", Ct);

            var result = await billing.FilePlaceAsync(from, conversationId: "pricing", cancellationToken: Ct);

            if (result["ok"]?.GetValue<bool>() == true)
            {
                admitted.Add($"place from '{from}' placed {result["name"]}");
            }
        }

        Assert.False(File.Exists(outsideTarget), "a file was written outside the repository");
        Assert.True(admitted.Count == 0, string.Join('\n', admitted));
    }
}
