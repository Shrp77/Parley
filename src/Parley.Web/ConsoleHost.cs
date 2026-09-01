namespace Parley.Web;

/// <summary>
/// Builds the operator's console, wherever it is being started from.
/// </summary>
/// <remarks>
/// <para>
/// The console renders what SPEC.md §10.2 grants a person, and binds the five operations of that
/// section that change something: retire, reinstate, instruct, purge and force-conclude. Each
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
/// <c>Main</c>, and <c>parley console</c>. Two callers building a host each is two hosts that
/// differ the first time one of them is edited, and the differences that matter — the loopback
/// default, the log providers, the content root — are exactly the ones nobody would notice.
/// </para>
/// </remarks>
public static class ConsoleHost
{
    /// <summary>Where the console listens when nothing says otherwise.</summary>
    /// <remarks>
    /// Loopback. SPEC.md §16 confines the transport to a trusted path because anything able to
    /// reach a broker can claim to be any agent, and this console is a second door onto that same
    /// path — it is not an authentication boundary and must not be mistaken for one. So the safe
    /// default is the one where the question does not arise, and widening it announces itself
    /// rather than happening quietly.
    /// </remarks>
    public const string LoopbackAddress = "http://127.0.0.1:5280";

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
        // This was found the hard way, by a stylesheet that 404d — and the eventual fix for that
        // was to stop having a file at all. The anchor stays because the host reads its content
        // root for more than assets, and because nothing in the test suite would catch it going
        // wrong: WebApplicationFactory sets the content root from the test project, so the tests
        // always look in the right place.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Requests and errors, never message text or payloads. §9.4's retention governs the
        // store; a copy of a message in a web log is governed by nothing and would outlive the
        // prune meant to reclaim it. See specs/003-parley-web-console FR-031.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();

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

        // The application part named rather than discovered. Razor Pages are found by walking the
        // entry assembly and its dependencies, and the entry assembly is `parley` when the
        // console is started as a subcommand — which finds the stylesheet, because that is a file
        // beside the binary, and finds no pages at all, because those are compiled into this
        // assembly. Every route 404s and nothing says why.
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
            new Seating.SeatBook(
                services.GetRequiredService<BrokerDirectory>().AgentsFor));

        var app = builder.Build();

        app.MapRazorPages();
        MapBrand(app);

        return app;
    }

    /// <summary>The two brand images, and the names they are reached by.</summary>
    /// <remarks>
    /// A fixed table rather than a name composed from the route. The alternative reads a
    /// manifest resource named by whatever a caller asked for, which is the same shape as
    /// serving a file named by a caller — and while assembly resources hold no directories to
    /// escape into, a lookup that can be pointed at anything is not worth having for the sake of
    /// two entries.
    /// </remarks>
    private static readonly (string Route, string Resource)[] Brand =
    [
        ("icon.png", "Parley.Web.Brand.icon.png"),
        ("wordmark.png", "Parley.Web.Brand.wordmark.png")
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
        foreach (var (route, resource) in Brand)
        {
            app.MapGet($"/brand/{route}", (HttpContext context) => Image(context, resource));
        }

        app.MapGet("/favicon.ico", (HttpContext context) => Image(context, Brand[0].Resource));
    }

    /// <summary>Reads one embedded image, as a response.</summary>
    /// <param name="context">The request being answered.</param>
    /// <param name="resource">The manifest resource name.</param>
    /// <returns>The image, or a 404 if the assembly does not carry it.</returns>
    /// <remarks>
    /// Cached for a day rather than for ever. The brand outlives any single run of the console
    /// so it is worth caching, and it is served from a name that does not change with its
    /// contents — so a cache that never expired would be one an operator could only clear by
    /// knowing to.
    /// </remarks>
    private static IResult Image(HttpContext context, string resource)
    {
        var stream = typeof(ConsoleHost).Assembly.GetManifestResourceStream(resource);

        if (stream is null)
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "public, max-age=86400";

        return Results.Stream(stream, "image/png", enableRangeProcessing: false);
    }

    /// <summary>Builds the console and runs it until it is stopped.</summary>
    /// <param name="args">The command line, as a host reads one.</param>
    /// <param name="address">Where to listen, or null for <see cref="LoopbackAddress"/>.</param>
    /// <param name="warn">Where to say that a non-loopback address was chosen.</param>
    /// <param name="fallbackBroker">
    /// The broker to offer when this machine's manifest names no projects. Null to offer none.
    /// </param>
    /// <returns>A task completing when the console stops.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static Task RunAsync(
        string[] args,
        string? address = null,
        TextWriter? warn = null,
        string? fallbackBroker = null) =>
        Build(args, address, warn, fallbackBroker).RunAsync();

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
