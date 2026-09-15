using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the two clients stay two clients, and that the one which can speak as a person is reached
/// through the one type that checks who they claim to be.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.9. FR-019 and the central claim of both operations contracts:
/// <c>ClientFor</c> is the operator's client and everything read <i>about</i> a deployment comes
/// from it; <c>AgentsFor</c> is the agent's client and is reached <b>only</b> through
/// <c>SeatBook</c>, which is the single type that checks a claimed name against the registry
/// before anything is sent under it.
/// </para>
/// <para>
/// The contract says the separation is the guarantee and the naming is not, and until now nothing
/// checked it — the same shape as the four operations run 3.7 found unlisted. A screen that reached
/// <c>AgentsFor</c> directly would be able to speak as a name nobody validated, which is the one
/// power SPEC.md §10.2 never grants an operator and the whole reason seating has a guard at all.
/// </para>
/// </remarks>
public sealed class ClientSeparationTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static IEnumerable<FileInfo> Terminal() =>
        new DirectoryInfo(Path.Combine(Root(), "src", "Parley.Terminal")).EnumerateFiles("*.cs");

    [Fact]
    public void The_agents_client_is_never_reached_without_a_seat_in_the_same_breath()
    {
        // Stated as the property rather than as the word, because the word is not the guarantee.
        // The client may be asked for while handing it to the book that validates a name, and it
        // may be asked for to speak through a seat the book has already granted. Anywhere else it
        // is an Agents client held by a screen, which can send under any name it likes.
        var loose = Terminal()
            .SelectMany(file => File.ReadAllLines(file.FullName)
                .Where(line => line.Contains("AgentsFor", StringComparison.Ordinal))
                .Where(line => !line.Contains("SeatBook", StringComparison.Ordinal)
                            && !line.Contains("Held.Seat", StringComparison.Ordinal))
                .Select(line => $"{file.Name}: {line.Trim()}"))
            .ToList();

        Assert.True(
            loose.Count == 0,
            "These reach the agents client without a seat: " + string.Join("; ", loose));
    }

    [Fact]
    public void A_voice_cannot_be_had_without_a_seat_that_was_granted()
    {
        // The other half of the same property, from the other end: every construction of the thing
        // that speaks names a held seat, so there is no path to one that skipped the book.
        var speaking = Terminal()
            .SelectMany(file => File.ReadAllLines(file.FullName)
                .Where(line => line.Contains("new SeatVoice", StringComparison.Ordinal))
                .Select(line => $"{file.Name}: {line.Trim()}"))
            .ToList();

        Assert.NotEmpty(speaking);
        Assert.All(speaking, line => Assert.Contains("Held.Seat", line, StringComparison.Ordinal));
    }

    [Fact]
    public void The_seat_is_the_only_thing_that_speaks_and_it_is_not_built_here()
    {
        // SeatVoice drains and records in one act, and settles the seat when the broker says the
        // exchange closed. A surface that assembled the same calls itself would be a second writer
        // of what a person has seen, which is how two surfaces come to disagree about it.
        var built = Terminal()
            .Where(file => Regex.IsMatch(
                File.ReadAllText(file.FullName),
                @"new\s+ParleyAgentsClient",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)))
            .Select(file => file.Name)
            .ToList();

        Assert.Empty(built);
    }

    [Fact]
    public void Only_the_shell_holds_the_directory_the_operator_client_comes_from()
    {
        // Run 3.4 removed a directory a region took and never used. This keeps it removed: a region
        // that could build its own client is a region that can read a project other than the one
        // the highlight is on.
        var holding = Terminal()
            .Where(file => file.Name != "Shell.cs")
            .Where(file => File.ReadAllText(file.FullName)
                .Contains("ClientFor", StringComparison.Ordinal))
            .Select(file => file.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            holding.Count == 0,
            "These build their own operator client: " + string.Join(", ", holding));
    }

    [Fact]
    public void A_seat_is_taken_and_released_through_the_book_and_nowhere_else()
    {
        // Joining and leaving are ordinary attachments per §4.0, and the book is what makes them
        // one fact rather than a flag each surface keeps to itself.
        var joining = Terminal()
            .Where(file => Regex.IsMatch(
                File.ReadAllText(file.FullName),
                @"\.(Take|Release)Async|\bnew\s+Seat\s*\(",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)))
            .Select(file => file.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            joining.Count <= 1,
            "More than one file here takes or makes a seat: " + string.Join(", ", joining));
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        var files = Terminal().ToList();

        Assert.NotEmpty(files);
        Assert.Contains(files, file => file.Name == "Shell.cs");
        Assert.Contains(
            "ClientFor",
            File.ReadAllText(files.Single(file => file.Name == "Shell.cs").FullName),
            StringComparison.Ordinal);
    }
}
