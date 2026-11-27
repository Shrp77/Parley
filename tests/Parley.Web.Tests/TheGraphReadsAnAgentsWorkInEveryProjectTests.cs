using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Parley.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>That the graph shows what waits for an agent in every project it serves, not only where its turn was given.</summary>
/// <remarks>
/// <para>
/// One checkout enrolled in two projects is one process, and the picture draws it as one agent (SPEC.md §4.1.1). A given
/// turn is reported only to the project that gave it, so in the other project the same agent's row reads idle. Who is at
/// work was decided project by project, and what the agent owed in the idle-reading project was never read: the picture
/// said "2 projects" and left out everything queued for it there (lens 1 run 3 on #115).
/// </para>
/// <para>
/// The control has the second row at work too, so the test fails only for the reason it names.
/// </para>
/// </remarks>
[Integration]
public sealed class TheGraphReadsAnAgentsWorkInEveryProjectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public Task What_waits_in_a_project_where_its_row_reads_idle_is_drawn_while_it_works_in_another() => DrawnAsync(busyInWarehouse: false);

    [Fact]
    public Task What_waits_in_a_project_where_it_is_at_work_too_is_drawn() => DrawnAsync(busyInWarehouse: true);

    private static async Task DrawnAsync(bool busyInWarehouse)
    {
        using var console = new TwoProjectConsole();
        var now = DateTimeOffset.UtcNow;
        var identity = AgentIdentity.Mint().ToString();

        // A turn shop gave runs; warehouse hears no turn.
        console.Shop.Registry.Add(new Wire.RegistryRow
        {
            Name = "billing@pc", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running, Connected = true, Identity = identity,
            TurnsReported = true, TurnRunningSinceUtc = Utc(now.AddMinutes(-4)),
        });
        console.Warehouse.Registry.Add(new Wire.RegistryRow
        {
            Name = "billing@pc", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running, Connected = true, Identity = identity,
            TurnsReported = true, LastTurnEndedUtc = Utc(now.AddHours(-1)), BackgroundTasks = busyInWarehouse ? 1 : 0,
        });
        console.Warehouse.Messages.Add(new Wire.Message
        {
            Id = 1, ExchangeId = 9, Type = Wire.MessageType.Request, From = "orders@pc", To = "billing@pc",
            PayloadJson = """{"ask":"Count the pallets"}""", ConversationId = "stock",
            CreatedUtc = Utc(now.AddMinutes(-2)),
        });
        console.Warehouse.Edges.Add(new Wire.WaitingEdge { Waiter = "orders@pc", WaitedOn = "billing@pc", ExchangeId = 9 });

        using var client = console.CreateClient();
        var fragment = await client.GetStringAsync(new Uri("/Graph", UriKind.Relative), Ct);

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(fragment, ">billing@pc</text>"));
        Assert.Contains("2 projects", fragment, StringComparison.Ordinal);
        Assert.Contains("QUEUED 1 · warehouse", fragment, StringComparison.Ordinal);
        Assert.Contains("Count the pallets", fragment, StringComparison.Ordinal);

        // Read as a record, which takes no delivery.
        Assert.All(console.Warehouse.ExportsAsked, asked => Assert.False(asked.LatestOnly));
    }

    /// <summary>A console with two projects, each answered by its own scripted broker.</summary>
    private sealed class TwoProjectConsole : WebApplicationFactory<Program>
    {
        public ScriptedExport Shop { get; } = new();

        public ScriptedExport Warehouse { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<BrokerDirectory>();
                services.AddSingleton(_ => new BrokerDirectory(
                    [
                        new ProjectSettings(ProjectName.Create("shop"), "shop.db", "http://127.0.0.1:5405", null),
                        new ProjectSettings(ProjectName.Create("warehouse"), "warehouse.db", "http://127.0.0.1:5406", null),
                    ],
                    address => GrpcChannel.ForAddress(
                        address,
                        new GrpcChannelOptions
                        {
                            HttpHandler = address.Contains("5406", StringComparison.Ordinal) ? Warehouse : Shop,
                            DisposeHttpClient = false,
                        })));
            });
        }
    }
}
