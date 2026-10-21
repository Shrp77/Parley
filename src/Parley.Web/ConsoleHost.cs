using Parley.Client;
using Parley.Configuration;



namespace Parley.Web;

/// <summary>
/// Builds the operator's console, wherever it is being started from.
/// </summary>
/// <remarks>
/// <para>
/// The console renders what SPEC.md §10.2 grants a person, and binds every operation of that
/// section. Each that changes something
/// describes what it will do before it does it, and none is reachable by following a link — a GET
/// describes and only a POST acts, so nothing that crawls a page left open can destroy anything.
/// </para>
/// <para>
/// It opens no database. §4 gives the store to one broker process, and a second reader of that
/// file is the corruption the section exists to prevent — so every fact rendered here arrives
/// over the transport, per request, from a broker.
/// </para>
/// <para>
/// Here rather than in the entry point because there are two callers: this assembly's own
/// <c>Main</c>, and <c>parley-web-ui</c>. Two callers building a host each is two hosts that
/// differ the first time one of them is edited, and the differences that matter — the loopback
/// default, the log providers, the content root — are exactly the ones nobody would notice.
/// </para>
/// </remarks>
public static class ConsoleHost
{
    /// <summary>Where the web UI listens when nothing says otherwise.</summary>
    /// <remarks>
    /// <see cref="WebUiAddress.Loopback"/>, which is where the number lives and where the reason
    /// for it is written down. Kept here as a name because this is the assembly that binds it, and
    /// a host that did not state its own default would send every reader to find out elsewhere.
    /// </remarks>
    public const string LoopbackAddress = WebUiAddress.Loopback;

    /// <summary>Builds the console.</summary>
    /// <param name="args">The command line, as a host reads one.</param>
    /// <param name="address">Where to listen, or null for <see cref="LoopbackAddress"/>.</param>
    /// <param name="warn">Where to say that a non-loopback address was chosen.</param>
    /// <param name="fallbackBroker">
    /// The broker to offer when this machine's manifest names no projects — what every other
    /// operator command would have connected to. Null to offer none.
    /// </param>
    /// <returns>The application, built and not yet running.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static WebApplication Build(
        string[] args,
        string? address = null,
        TextWriter? warn = null,
        string? fallbackBroker = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        // ContentRootPath anchored to the executable rather than to wherever it was launched
        // from. The default is the current directory, which is right for `dotnet run` in a
        // project folder and wrong for every other way this is started: an operator running it
        // from their repository, or a scheduled task with no meaningful working directory.
        //
        // The host reads its content root for more than assets, and nothing in the test suite
        // would catch it going wrong: WebApplicationFactory sets the content root from the test
        // project, so the tests always look in the right place.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Requests and errors, never message text or payloads. §9.4's retention governs the
        // store; a copy of a message in a web log is governed by nothing and would outlive the
        // prune meant to reclaim it.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();

        // Quiet by default, like the broker: per-request routing narration would bury a log that
        // is meant to be read.
        //
        // Nothing diagnostic is lost. A page that throws logs at Error, a bind that fails logs at
        // Error, and the commands that start this each print the address themselves.
        //
        // Raisable rather than final: the host binds `Logging` from configuration, so
        // `Logging__LogLevel__Default=Information` in the environment restores every line.
        // This is a floor and not a ceiling, because the one time somebody wants that narration is
        // the time they cannot rebuild to get it.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var chosen = address
            ?? builder.Configuration["urls"]
            ?? builder.Configuration["ASPNETCORE_URLS"];

        if (chosen is null)
        {
            builder.WebHost.UseUrls(LoopbackAddress);
        }
        else
        {
            builder.WebHost.UseUrls(chosen);

            if (!IsLoopback(chosen))
            {
                (warn ?? Console.Error).WriteLine(
                    "Parley console: binding an address that is not loopback. Anything able to "
                    + "reach a broker can claim to be any agent (SPEC.md §16); this console is "
                    + "not an authentication boundary.");
            }
        }

        // **Only requests that name this console's own host are served.** With no
        // allow-list the host accepts any Host header, so a page whose own name an attacker points at 127.0.0.1 — DNS
        // rebinding — is same-origin with this console in the person's browser: it reads a form, takes its antiforgery
        // token and posts it back. Antiforgery is the only thing between a
        // browser and purge and every transcript, so the Host is checked before anything else. Set only when
        // the deployment has not chosen its own `AllowedHosts`.
        builder.Configuration["AllowedHosts"] ??= AllowedHosts(chosen);

        // The application part named rather than discovered. Razor Pages are found by walking the
        // entry assembly and its dependencies, and the entry assembly is not this one when a
        // test or another tool hosts it, which would find no pages, because those are compiled
        // into this assembly. Every route would 404 and nothing would say why.
        builder.Services
            .AddRazorPages()
            .AddApplicationPart(typeof(ConsoleHost).Assembly);

        builder.Services.AddSingleton(_ => BrokerDirectory.FromManifest(
            null,
            fallbackBroker is { Length: > 0 } offer ? new FallbackBroker(offer) : null));

        // A singleton because a seat is an open connection that has to outlive the request which
        // took it, and disposed with the host because an attachment that outlives the console is
        // a person the deployment still believes is present.
        builder.Services.AddSingleton(services =>
            new Parley.Client.Seating.SeatBook(
                services.GetRequiredService<BrokerDirectory>().AgentsFor));

        // What the console has told the person, for the bell. One for the process, because one console serves one person.
        builder.Services.AddSingleton<Notifications.NotificationLog>();
        builder.Services.AddSingleton<EnrolmentRuns>();

        var app = builder.Build();

        // **No other page may frame this one.** The console can purge an agent and read every transcript, and a page in
        // another tab that frames it can place its own buttons over the console's and have the person press them
        // (clickjacking). `frame-ancestors` is the current control and `X-Frame-Options` the one older browsers read.
        // The rest of the policy is only what costs nothing: the pages carry inline scripts, so a script policy would
        // need a nonce on every one of them, and none is claimed here.
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            headers["X-Frame-Options"] = "DENY";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "frame-ancestors 'none'; base-uri 'none'; object-src 'none'; form-action 'self'";

            await next(context).ConfigureAwait(false);
        });

        app.MapRazorPages();
        MapBrand(app);

        return app;
    }

    /// <summary>The brand images and the icon font, and the names they are reached by.</summary>
    /// <remarks>
    /// A fixed table rather than a name composed from the route. The alternative reads a
    /// manifest resource named by whatever a caller asked for, which is the same shape as
    /// serving a file named by a caller — and while assembly resources hold no directories to
    /// escape into, a lookup that can be pointed at anything is not worth having for the sake of
    /// three entries. The font is Material Symbols Outlined (Apache License 2.0), cut down to
    /// the icons the console names; see the comment beside it in the layout.
    /// </remarks>
    private static readonly (string Route, string Resource, string ContentType)[] Brand =
    [
        ("icon.png", "Parley.Web.Brand.icon.png", "image/png"),
        ("wordmark.png", "Parley.Web.Brand.wordmark.png", "image/png"),
        ("material-symbols.woff2", "Parley.Web.Brand.material-symbols.woff2", "font/woff2")
    ];

    /// <summary>Serves the brand images out of the assembly.</summary>
    /// <param name="app">The application to add the routes to.</param>
    /// <remarks>
    /// <c>/favicon.ico</c> is mapped alongside because a browser asks for it whether or not the
    /// page names one, and an unmapped one is a 404 in the log of every console for ever. It is
    /// answered with the PNG, which every browser this console runs in accepts.
    /// </remarks>
    private static void MapBrand(WebApplication app)
    {
        foreach (var (route, resource, contentType) in Brand)
        {
            app.MapGet($"/brand/{route}", (HttpContext context) => Image(context, resource, contentType));
        }

        app.MapGet("/favicon.ico", (HttpContext context) => Image(context, Brand[0].Resource, Brand[0].ContentType));
    }

    /// <summary>Reads one embedded file, as a response.</summary>
    /// <param name="context">The request being answered.</param>
    /// <param name="resource">The manifest resource name.</param>
    /// <param name="contentType">The media type to answer with.</param>
    /// <returns>The file, or a 404 if the assembly does not carry it.</returns>
    /// <remarks>
    /// Cached for a day rather than for ever. The brand outlives any single run of the console
    /// so it is worth caching, and it is served from a name that does not change with its
    /// contents — so a cache that never expired would be one an operator could only clear by
    /// knowing to.
    /// </remarks>
    private static IResult Image(HttpContext context, string resource, string contentType)
    {
        var stream = typeof(ConsoleHost).Assembly.GetManifestResourceStream(resource);

        if (stream is null)
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "public, max-age=86400";

        return Results.Stream(stream, contentType, enableRangeProcessing: false);
    }

    /// <summary>Builds the console and runs it until it is stopped.</summary>
    /// <param name="args">The command line, as a host reads one.</param>
    /// <param name="address">Where to listen, or null for <see cref="LoopbackAddress"/>.</param>
    /// <param name="warn">Where to say that a non-loopback address was chosen.</param>
    /// <param name="fallbackBroker">
    /// The broker to offer when this machine's manifest names no projects. Null to offer none.
    /// </param>
    /// <param name="cancellationToken">Stops the console.</param>
    /// <returns>A task completing when the console stops.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static async Task RunAsync(
        string[] args,
        string? address = null,
        TextWriter? warn = null,
        string? fallbackBroker = null,
        CancellationToken cancellationToken = default)
    {
        var app = Build(args, address, warn, fallbackBroker);

        // Started and waited on separately rather than through RunAsync, which takes no token,
        // so the caller's cancellation stops the console: one that ignored the interrupt would
        // hold its process open after everything an agent depends on had stopped.
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Stopped explicitly rather than left to the wait, which stops the host on some paths
            // out and not on others: a console nobody stopped would go on serving a deployment
            // that had shut down.
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The Host names this console answers to, separated as <c>AllowedHosts</c> reads them.</summary>
    /// <param name="chosen">The address it binds, or null for the loopback default.</param>
    /// <returns>The loopback names, and each host the bind address names.</returns>
    /// <remarks>
    /// A bind on every interface (<c>*</c>, <c>+</c>, <c>0.0.0.0</c>, <c>[::]</c>) names no host, so this machine's own
    /// name is allowed for it — what a person on the network types — and never a wildcard, which would be the rebinding
    /// hole again. Somebody who reaches it by an IP address sets <c>AllowedHosts</c> themselves, and the host keeps it.
    /// </remarks>
    internal static string AllowedHosts(string? chosen)
    {
        var hosts = new List<string> { "localhost", "127.0.0.1", "[::1]" };

        foreach (var one in (chosen ?? string.Empty)
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var host = Uri.TryCreate(one.Replace("://*", "://wildcard", StringComparison.Ordinal)
                                        .Replace("://+", "://wildcard", StringComparison.Ordinal), UriKind.Absolute, out var uri)
                ? uri.Host
                : null;

            if (host is null or "wildcard" or "0.0.0.0" or "[::]")
            {
                hosts.Add(Environment.MachineName);
            }
            else
            {
                hosts.Add(host);
            }
        }

        return string.Join(';', hosts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Whether an address reaches only this machine.</summary>
    /// <param name="address">The address a host was asked to bind.</param>
    /// <returns><see langword="true"/> when nothing off this machine can reach it.</returns>
    /// <remarks>
    /// Judged by what was written rather than by resolving it, because the warning is about what
    /// the operator asked for. Anything this does not recognise is treated as reachable, so a
    /// spelling nobody anticipated produces a warning that is merely unnecessary rather than a
    /// silence that is wrong.
    /// </remarks>
    private static bool IsLoopback(string address) =>
        address.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(one =>
                one.Contains("127.0.0.1", StringComparison.Ordinal)
                || one.Contains("[::1]", StringComparison.Ordinal)
                || one.Contains("localhost", StringComparison.OrdinalIgnoreCase));
}
