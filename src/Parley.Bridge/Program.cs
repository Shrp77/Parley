using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Parley.Bridge;
using Parley.Core;

var (options, optionError) = BridgeOptions.Parse(args);

if (options is null)
{
    await Console.Error.WriteLineAsync(optionError).ConfigureAwait(false);

    await Console.Error.WriteLineAsync(
        """

        usage: parley-bridge [--db <path>] [--port <n>] [--listen-on-all-interfaces]

        Watches a Parley deployment and, for agents that recorded a launch command,
        starts them and shows their streams. Not required: Parley runs unchanged
        without it.
        """).ConfigureAwait(false);

    return 2;
}

var (store, storeError) = StoreAccess.Open(options.DatabasePath);

if (store is null)
{
    await Console.Error.WriteLineAsync(storeError).ConfigureAwait(false);
    return 3;
}

using (store)
{
    var agents = new AgentHost();
    await using var closingAgents = agents.ConfigureAwait(false);

    var origin = string.Create(
        CultureInfo.InvariantCulture, $"http://{BridgeOptions.Loopback}:{options.Port}");

    var builder = WebApplication.CreateSlimBuilder(args);

    builder.WebHost.UseUrls(string.Create(
        CultureInfo.InvariantCulture, $"http://{options.Address}:{options.Port}"));

    builder.Logging.ClearProviders();

    var app = builder.Build();

    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    // Every subscriber's queue. Unbounded is safe because a browser tab drains continuously and
    // the writer is one operator's fleet; bounded would silently drop the line that mattered.
    var subscribers = new List<Channel<string>>();

    void Broadcast(string kind, object payload)
    {
        var frame = string.Create(
            CultureInfo.InvariantCulture,
            $"event: {kind}\ndata: {JsonSerializer.Serialize(payload, json)}\n\n");

        lock (subscribers)
        {
            foreach (var subscriber in subscribers)
            {
                subscriber.Writer.TryWrite(frame);
            }
        }
    }

    agents.LineWritten += line => Broadcast("line", line);

    // ---------------------------------------------------------------------------------------
    // The gate. Loopback is not a security boundary for a web application: any page in any tab
    // can reach 127.0.0.1, and this process can start agents and type into them.
    // ---------------------------------------------------------------------------------------
    app.Use(async (context, next) =>
    {
        // Refused before the token is looked at: a rebound name resolving here is same-origin to
        // the browser, so the Origin check below never fires for it.
        if (!AccessToken.AddressedThisMachine(
                context.Request.Headers.Host.FirstOrDefault(), options.IsExposed))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Forbidden.").ConfigureAwait(false);
            return;
        }

        // Allow-listed, never deny-listed: only the page may carry its token in the query, and
        // only because a browser fetching a typed URL cannot set a header. See
        // AccessToken.MayPresentTokenInQuery for the bypass the other shape had.
        var presented = AccessToken.MayPresentTokenInQuery(context.Request.Path.Value)
            ? context.Request.Query["t"].FirstOrDefault()
            : context.Request.Headers[AccessToken.Header].FirstOrDefault();

        if (!AccessToken.Admits(
                presented, options.Token, context.Request.Headers.Origin.FirstOrDefault(), origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Forbidden.").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    });

    app.MapGet("/", (HttpContext context) =>
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(Ui.Page);
    });

    app.MapGet("/api/fleet", (CancellationToken token) =>
        store.UseAsync(s => s.View.ReadAsync(agents.Names, token), token));

    app.MapGet("/api/agents/{name}/transcript", (string name) =>
        Results.Ok(agents.Transcript(name)));

    // ---------------------------------------------------------------------------------------
    // Live updates. The browser is pushed to and polls nothing; the bridge notices commits with
    // one pragma against a local file. Delivered over a plain streamed response rather than
    // EventSource, because EventSource cannot set a header and the token must not travel in a
    // query string on the long-lived connection.
    // ---------------------------------------------------------------------------------------
    app.MapGet("/api/stream", async (HttpContext context, CancellationToken token) =>
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        var mine = Channel.CreateUnbounded<string>();

        lock (subscribers)
        {
            subscribers.Add(mine);
        }

        try
        {
            await foreach (var frame in mine.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await context.Response.WriteAsync(frame, token).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The tab closed. Ordinary.
        }
        finally
        {
            lock (subscribers)
            {
                subscribers.Remove(mine);
            }
        }
    });

    // ---------------------------------------------------------------------------------------
    // Operator operations. Every one of these is an OperatorConsole method reached over HTTP
    // rather than over argv: no new power, and the same boundary tests still guard it.
    // ---------------------------------------------------------------------------------------
    app.MapPost("/api/agents/{name}/retire", (string name, CancellationToken token) =>
        WithAgent(name, (s, a) => s.Console.RetireAsync(a, token)));

    app.MapPost("/api/agents/{name}/reinstate", (string name, CancellationToken token) =>
        WithAgent(name, (s, a) => s.Console.ReinstateAsync(a, token)));

    app.MapPost("/api/agents/{name}/purge", async (string name, CancellationToken token) =>
    {
        if (!AgentName.TryCreate(name, out var agent))
        {
            return Results.BadRequest($"'{name}' is not an agent name.");
        }

        var outcome = await store
            .UseAsync(s => s.Console.PurgeAsync(agent, token), token)
            .ConfigureAwait(false);

        Broadcast("changed", new { reason = "purge" });

        // The holds are the point. A purge refused because something still references the agent
        // has to say what, or the operator's only recourse is to guess and try again.
        return Results.Ok(new
        {
            verdict = outcome.Verdict.ToString(),
            held = outcome.Holds.Select(h => new { h.Table, h.Column, h.Rows })
        });
    });

    // ---------------------------------------------------------------------------------------
    // Hosting. Only agents started here can have their streams read: a process's output belongs
    // to whoever spawned it.
    // ---------------------------------------------------------------------------------------
    app.MapPost("/api/agents/{name}/start", async (string name, CancellationToken token) =>
    {
        var (workspace, command) = await store
            .UseAsync(s => s.LaunchAsync(name, token), token).ConfigureAwait(false);

        var failure = agents.Start(name, workspace, command);

        Broadcast("changed", new { reason = "start" });

        return failure is null ? Results.Ok() : Results.BadRequest(failure);
    });

    app.MapPost("/api/agents/{name}/stop", async (string name) =>
    {
        var failure = await agents.StopAsync(name).ConfigureAwait(false);

        Broadcast("changed", new { reason = "stop" });

        return failure is null ? Results.Ok() : Results.BadRequest(failure);
    });

    app.MapPost("/api/agents/{name}/send", (string name, SendRequest request) =>
    {
        var failure = agents.Send(name, request?.Text ?? "");

        return failure is null ? Results.Ok() : Results.BadRequest(failure);
    });

    async Task<IResult> WithAgent(string name, Func<StoreAccess, AgentName, Task<bool>> work)
    {
        if (!AgentName.TryCreate(name, out var agent))
        {
            return Results.BadRequest($"'{name}' is not an agent name.");
        }

        var done = await store.UseAsync(s => work(s, agent)).ConfigureAwait(false);

        Broadcast("changed", new { reason = "operator" });

        return done ? Results.Ok() : Results.NotFound($"No agent named '{name}'.");
    }

    // ---------------------------------------------------------------------------------------
    // The watch. One pragma, on a schedule, against a local file -- not a poll of any agent.
    // ---------------------------------------------------------------------------------------
    using var watch = new StoreWatch(options.DatabasePath);
    using var stopping = new CancellationTokenSource();

    var watching = Task.Run(async () =>
    {
        using var ticks = new PeriodicTimer(StoreWatch.Interval);

        try
        {
            while (await ticks.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
            {
                if (watch.Changed())
                {
                    Broadcast("changed", new { reason = "store" });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    });

    var banner = new StringBuilder()
        .Append(CultureInfo.InvariantCulture, $"Parley bridge on http://{options.Address}:{options.Port}/?t={options.Token}")
        .AppendLine()
        .Append(CultureInfo.InvariantCulture, $"Watching {options.DatabasePath}");

    if (options.IsExposed)
    {
        banner.AppendLine().AppendLine()
            .Append(
                "  WARNING: listening on every interface. This process can start agents and " +
                "type into them,\n  which is remote code execution in every repository it can " +
                "reach. The token is the only thing\n  between the network and that.");
    }

    Console.WriteLine(banner.ToString());

    await app.RunAsync().ConfigureAwait(false);

    await stopping.CancelAsync().ConfigureAwait(false);
    await watching.ConfigureAwait(false);
}

return 0;

/// <summary>What to say to a hosted agent.</summary>
/// <param name="Text">The turn's text.</param>
internal sealed record SendRequest(string Text);
