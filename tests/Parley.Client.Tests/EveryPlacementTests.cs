using System.Reflection;
using Parley.Client.Files;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That a conversation's Media and transcript read every page of its files, and only its own (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// A broker that answers in pages, from a stand-in: a real one would need more than a page's worth of placements, two
/// hundred files, to show a second page at all.
/// </para>
/// <para>
/// The broker already lists only the conversation asked for, so the second check holds the client's own filter against
/// a broker that does not: a file of another conversation drawn in this one's transcript would be shown under words it
/// was never placed beside.
/// </para>
/// </remarks>
[Unit]
public sealed class EveryPlacementTests
{
    private static readonly DateTimeOffset Ten = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private static SharedFile File(string conversation, string name, DateTimeOffset at) => new(
        conversation, conversation, SharedFileName.Create(name), 2048, ContentHash.Of(System.Text.Encoding.UTF8.GetBytes(name)),
        AgentName.Create("billing@test"), at);

    private static SharedFileListing Page(string? next, params SharedFile[] files) =>
        new(new SharedFilePage(files, [], files.Length, files.Length * 2048L, next), []);

    [Fact]
    public async Task Every_page_is_read_until_the_broker_says_there_is_no_more()
    {
        var broker = Pages.Over(new Dictionary<string, SharedFileListing>
        {
            [string.Empty] = Page("2", File("pricing", "first.png", Ten)),
            ["2"] = Page("3", File("pricing", "second.md", Ten.AddMinutes(1))),
            ["3"] = Page(null, File("pricing", "third.txt", Ten.AddMinutes(2))),
        });

        var files = await Placements.EveryAsync(broker, "pricing", TestContext.Current.CancellationToken);

        Assert.Equal(["first.png", "second.md", "third.txt"], files.Select(f => f.Name.Value));
    }

    [Fact]
    public async Task A_file_of_another_conversation_is_left_out()
    {
        var broker = Pages.Over(new Dictionary<string, SharedFileListing>
        {
            [string.Empty] = Page(null, File("pricing", "mockup.png", Ten), File("tax-questions", "elsewhere.png", Ten.AddMinutes(1))),
        });

        var files = await Placements.EveryAsync(broker, "pricing", TestContext.Current.CancellationToken);

        Assert.Equal(["mockup.png"], files.Select(f => f.Name.Value));
    }

    /// <summary>A broker that answers a listing from pages keyed by the token asked with, and nothing else.</summary>
    public class Pages : DispatchProxy
    {
        private IReadOnlyDictionary<string, SharedFileListing> _pages = new Dictionary<string, SharedFileListing>();

        /// <summary>A stand-in broker over these pages.</summary>
        /// <param name="pages">Each page, by the token that asks for it; the first under the empty string.</param>
        /// <returns>The broker.</returns>
        public static IParleyBrokerClient Over(IReadOnlyDictionary<string, SharedFileListing> pages)
        {
            var broker = Create<IParleyBrokerClient, Pages>();

            ((Pages)(object)broker)._pages = pages;

            return broker;
        }

        /// <inheritdoc/>
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IParleyBrokerClient.ListSharedFilesAsync)
                ? Task.FromResult(_pages[(string?)args![2] ?? string.Empty])
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
