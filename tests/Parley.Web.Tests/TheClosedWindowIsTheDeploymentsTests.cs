using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Parley.Configuration;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>The Inbox's Closed window is saved in the deployment's settings.json and read from it.</summary>
/// <remarks>
/// The settings page said "Saved ... here and in the terminal" while nothing tested that it wrote the file, or that the
/// Inbox read it: either could stop working with every test green (review, lens 3 round 19).
/// </remarks>
[Integration]
public sealed partial class TheClosedWindowIsTheDeploymentsTests : IDisposable
{
    private const string Orders = "orders@" + RoadmapConsole.Project;
    private const string Person = "human-user@" + RoadmapConsole.Project;

    private readonly string _root = Directory.CreateTempSubdirectory("parley-closed-window").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex Token();

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
    public async Task Saving_the_window_writes_it_to_the_deployment()
    {
        using var console = new RoadmapConsole { Root = _root };
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Settings", UriKind.Relative), Ct);
        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(page).Groups[1].Value),
            new("Days", "30"),
        ]);

        using var response = await client.PostAsync(new Uri("/Settings?handler=Inbox", UriKind.Relative), form, Ct);

        Assert.Contains("Saved.", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(30, ParleySettings.ReadClosedDays(_root));
    }

    [Fact]
    public async Task Saving_the_window_with_no_number_asks_for_one_and_writes_nothing()
    {
        using var console = new RoadmapConsole { Root = _root };
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Settings", UriKind.Relative), Ct);
        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(page).Groups[1].Value),
            new("Days", string.Empty),
        ]);

        using var response = await client.PostAsync(new Uri("/Settings?handler=Inbox", UriKind.Relative), form, Ct);

        Assert.Contains("Enter a whole number of days from 1 to 365.", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, ParleySettings.FileName)), "a window was written although none was given");
    }

    [Fact]
    public async Task The_inbox_reads_its_window_from_the_deployment()
    {
        // Closed twenty days ago: outside the default week, inside the thirty days the deployment says.
        Assert.Null(ParleySettings.WriteClosedDays(_root, 30));

        using var console = new RoadmapConsole { Root = _root };

        console.Broker.Seats = true;
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Person, Adapter = "parley-console", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
        });
        console.Broker.Conversations.Add(new Wire.ConversationSummary
        {
            ConversationId = "talk",
            LastActivityUtc = DateTimeOffset.UtcNow.AddDays(-21).ToString("O", CultureInfo.InvariantCulture),
            LastClosedUtc = DateTimeOffset.UtcNow.AddDays(-20).ToString("O", CultureInfo.InvariantCulture),
        });
        console.Broker.ExportMessages.Add(new Wire.Message
        {
            Id = 1,
            ExchangeId = 20,
            Type = Wire.MessageType.Request,
            From = Orders,
            To = Person,
            ResponderModel = "test",
            PayloadJson = "{\"ask\":" + JsonSerializer.Serialize("Turn on the nightly export.") + "}",
            ConversationId = "talk",
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-21).ToString("O", CultureInfo.InvariantCulture),
        });
        console.Broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
        {
            ExchangeId = 20,
            State = Wire.ExchangeState.Closed,
            OpenedUtc = DateTimeOffset.UtcNow.AddDays(-21).ToString("O", CultureInfo.InvariantCulture),
            ClosedUtc = DateTimeOffset.UtcNow.AddDays(-20).ToString("O", CultureInfo.InvariantCulture),
            Outcome = Wire.ExchangeOutcome.Settled,
        });

        using var client = console.CreateClient();
        var closed = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);

        Assert.Contains("Closed in the last 30 days.", closed, StringComparison.Ordinal);
        Assert.Contains("Turn on the nightly export.", closed, StringComparison.Ordinal);
    }
}
