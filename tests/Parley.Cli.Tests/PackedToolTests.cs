using System.IO.Compression;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What the broker's tool actually ships, read out of the package rather than off a project file.
/// </summary>
/// <remarks>
/// <para>
/// SC-005. A project file says what a project references directly; a package says what lands on a
/// machine, and those are different questions — a transitive reference acquired through anything
/// would pass a reading of the csproj and still put the web UI's assemblies on every broker.
/// </para>
/// <para>
/// <b>SC-005 says "carries no part of the web stack", and that is narrowed here rather than
/// quietly reinterpreted.</b> The broker serves gRPC over Kestrel and always has:
/// <c>Grpc.AspNetCore.Server</c> is in this package because <c>Parley.Server</c> needs it, which
/// is the transport rather than a surface, and no packaging change could remove it without
/// changing how the broker speaks. What this feature removes is the web UI's own half — the pages,
/// the Razor runtime, and <c>Parley.Web</c> itself — and that is what is asserted.
/// </para>
/// <para>
/// The package is the one <c>dotnet pack</c> leaves in <c>bin/Release</c>, so this test skips
/// rather than fails when nothing has been packed: a suite that demanded a Release pack would fail
/// on a clean clone for a reason that has nothing to do with the code.
/// </para>
/// </remarks>
public sealed class PackedToolTests
{
    [Fact]
    public void The_brokers_package_carries_no_part_of_the_web_ui()
    {
        var package = Packed("Parley.Cli");

        if (package is null)
        {
            Assert.Skip("nothing packed; run `dotnet pack src/Parley.Cli -c Release` to check this.");

            return;
        }

        using var archive = ZipFile.OpenRead(package);

        var assemblies = archive.Entries
            .Select(entry => Path.GetFileName(entry.FullName))
            .Where(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Non-vacuity first: an empty listing would make every assertion below pass.
        Assert.Contains(assemblies, name => name == "Parley.Broker.dll");
        Assert.Contains(assemblies, name => name == "Parley.Store.dll");

        var webUi = assemblies
            .Where(name =>
                name.Contains("Parley.Web", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Razor", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Mvc", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            webUi.Count == 0,
            "the broker's package carries the web UI: " + string.Join(", ", webUi));
    }

    [Fact]
    public void The_terminals_package_carries_no_part_of_the_web_ui_either()
    {
        // The same question of the other surface, because they are installed independently and a
        // machine that wanted only a terminal should not acquire a web stack to get one.
        var package = Packed("Parley.Terminal");

        if (package is null)
        {
            Assert.Skip("nothing packed; run `dotnet pack src/Parley.Terminal -c Release` to check this.");

            return;
        }

        using var archive = ZipFile.OpenRead(package);

        var assemblies = archive.Entries
            .Select(entry => Path.GetFileName(entry.FullName))
            .Where(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Contains(assemblies, name => name == "Terminal.Gui.dll");

        var wrong = assemblies
            .Where(name =>
                name.Contains("Parley.Web", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Razor", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Parley.Store", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            wrong.Count == 0,
            "the terminal's package carries what it must not: " + string.Join(", ", wrong));
    }

    /// <summary>The newest package a project has produced, if any.</summary>
    /// <param name="package">The package id, which is also the project directory's name.</param>
    /// <returns>The path, or <see langword="null"/> when nothing has been packed.</returns>
    private static string? Packed(string package)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Parley.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var output = Path.Combine(directory!.FullName, "src", package, "bin", "Release");

        if (!Directory.Exists(output))
        {
            return null;
        }

        return new DirectoryInfo(output)
            .EnumerateFiles($"{package}.*.nupkg")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.FullName;
    }
}
