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

    [Fact]
    public async Task What_waits_in_the_first_project_read_is_drawn_while_it_works_in_a_later_one()
    {
        // Every project is read before who is at work is decided: the turn running in warehouse, read second, still
        // makes shop's owed work part of the picture.
        using var console = new TwoProjectConsole();
        var now = DateTimeOffset.UtcNow;
        var identity = AgentIdentity.Mint().ToString();

        console.Warehouse.Registry.Add(Row("billing@pc", identity, now, running: true));
        console.Shop.Registry.Add(Row("billing@pc", identity, now, running: false));
        Owes(console.Shop, "billing@pc", "Price the new range", now);

        var fragment = await GraphAsync(console);

        Assert.Contains("QUEUED 1 · shop", fragment, StringComparison.Ordinal);
        Assert.Contains("Price the new range", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_checkout_enrolled_under_another_name_in_each_project_is_one_agent_whose_work_is_read_in_both()
    {
        // One repository may be enrolled in several projects under different names: it is one agent by its identity and
        // machine, never by its name.
        using var console = new TwoProjectConsole();
        var now = DateTimeOffset.UtcNow;
        var identity = AgentIdentity.Mint().ToString();

        console.Shop.Registry.Add(Row("billing@pc", identity, now, running: true));
        console.Warehouse.Registry.Add(Row("ledger@pc", identity, now, running: false));
        Owes(console.Warehouse, "ledger@pc", "Count the pallets", now);

        var fragment = await GraphAsync(console);

        Assert.Contains("2 projects", fragment, StringComparison.Ordinal);
        Assert.Contains("Count the pallets", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_background_task_opens_the_agents_page_in_the_project_that_named_them()
    {
        // The turn runs in shop, so shop is where its name links; warehouse named its tasks, and shop named none.
        using var console = new TwoProjectConsole();
        var now = DateTimeOffset.UtcNow;
        var identity = AgentIdentity.Mint().ToString();
        var busy = Row("billing@pc", identity, now, running: true);
        busy.BackgroundTasks = 21;
        var naming = Row("billing@pc", identity, now, running: false);
        naming.BackgroundTasks = 21;
        naming.RunningTasks.AddRange(Enumerable.Range(1, 21).Select(i => new Wire.BackgroundTaskRow
        {
            Id = $"t{i}", Kind = Wire.TaskKind.Agent, Name = $"Task {i}", Said = true,
        }));
        console.Shop.Registry.Add(busy);
        console.Warehouse.Registry.Add(naming);

        var fragment = await GraphAsync(console);

        Assert.Contains(">Task 1</text>", fragment, StringComparison.Ordinal);
        Assert.Contains("<a href=\"/Projects/Agent?project=shop&amp;agent=billing%40pc\"><text", fragment, StringComparison.Ordinal);

        const string There = "href=\"/Projects/Agent?project=warehouse&amp;agent=billing%40pc#background-work\"";

        // The box counting the rest ("see all") and the list's "Every background task" both go there.
        Assert.Contains(There + "><text", fragment, StringComparison.Ordinal);
        Assert.Contains(There + ">Every background task, on its page.</a>", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("project=shop&amp;agent=billing%40pc#background-work", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_frozen_in_one_of_its_two_projects_is_drawn_frozen()
    {
        using var console = new TwoProjectConsole();
        var now = DateTimeOffset.UtcNow;
        var identity = AgentIdentity.Mint().ToString();
        var frozen = Row("billing@pc", identity, now, running: true);
        frozen.FrozenUtc = Utc(now.AddMinutes(-5));
        console.Shop.Registry.Add(frozen);
        console.Warehouse.Registry.Add(Row("billing@pc", identity, now, running: false));

        var fragment = await GraphAsync(console);

        Assert.Contains("<g class=\"pg-node pg-agent pg-frozen\">", fragment, StringComparison.Ordinal);
        Assert.Contains(">Unfreeze in shop</a>", fragment, StringComparison.Ordinal);
        Assert.Contains(">Freeze in warehouse</a>", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_freeze_link_names_the_agent_by_its_name_in_a_project_where_it_has_another()
    {
        using var console = new TwoProjectConsole();
        var now = DateTimeOffset.UtcNow;
        var identity = AgentIdentity.Mint().ToString();
        console.Shop.Registry.Add(Row("billing@pc", identity, now, running: true));
        console.Warehouse.Registry.Add(Row("checkout@pc", identity, now, running: false));

        var fragment = await GraphAsync(console);

        Assert.Contains("subject=checkout%40pc\">Freeze checkout@pc in warehouse</a>", fragment, StringComparison.Ordinal);
        Assert.Contains("subject=billing%40pc\">Freeze in shop</a>", fragment, StringComparison.Ordinal);
    }

    private static Wire.RegistryRow Row(string name, string identity, DateTimeOffset now, bool running)
    {
        var row = new Wire.RegistryRow
        {
            Name = name, Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running, Connected = true, Identity = identity, TurnsReported = true,
        };

        if (running)
        {
            row.TurnRunningSinceUtc = Utc(now.AddMinutes(-4));
        }
        else
        {
            row.LastTurnEndedUtc = Utc(now.AddHours(-1));
        }

        return row;
    }

    private static void Owes(ScriptedExport project, string agent, string ask, DateTimeOffset now)
    {
        project.Messages.Add(new Wire.Message
        {
            Id = 1, ExchangeId = 9, Type = Wire.MessageType.Request, From = "orders@pc", To = agent,
            PayloadJson = $$"""{"ask":"{{ask}}"}""", ConversationId = "stock",
            CreatedUtc = Utc(now.AddMinutes(-2)),
        });
        project.Edges.Add(new Wire.WaitingEdge { Waiter = "orders@pc", WaitedOn = agent, ExchangeId = 9 });
    }

    private static async Task<string> GraphAsync(TwoProjectConsole console)
    {
        using var client = console.CreateClient();

        return await client.GetStringAsync(new Uri("/Graph", UriKind.Relative), Ct);
    }

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
