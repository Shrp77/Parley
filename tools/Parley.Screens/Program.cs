using System.Globalization;
using System.Text;
using Parley.Screens;

const string Usage = """
    parley-screens: screenshots of the web console, from a broker of its own.

      parley-screens check <round>
      parley-screens capture <round> [--out <dir>] [--only <file,...>] [--browser msedge|chrome|chromium]
                                     [--stylesheet <file.css>]
      parley-screens hold <round> [--browser ...]
      parley-screens audit <round> [--only <file,...>] [--browser ...] [--stylesheet <file.css>]
                                   [--device http://127.0.0.1:9222]

    <round> is a folder holding scenario.json (the sample data, played into the broker) and shots.json
    (what to capture). check reads both and says what is wrong, starting nothing. capture writes each PNG
    and sha256.txt to --out, by default artifacts/screens/<round's folder name> in this repository. hold
    plays the scenario, prints the console's address and waits for Enter. audit opens each shot's page and
    lists what is wider than the window and every control that cannot be reached, writing no PNG.

    Exit codes: 0 done; 1 a shot failed; 64 usage; 65 a round file is wrong; 69 no browser;
    70 the broker refused a step or a deployment could not start; 130 stopped.
    Nothing here reads or changes the machine's own deployment. See tools/Parley.Screens/README.md.
    """;

if (args.Length < 2 || args[0] is not ("check" or "capture" or "hold" or "audit"))
{
    Console.Error.WriteLine(Usage);
    return 64;
}

var command = args[0];
var round = Path.GetFullPath(args[1]);
var options = Options(args.Skip(2).ToArray());

// audit writes nothing, so a folder to write to is a mistake rather than something to ignore.
if (options is null || (string.Equals(command, "audit", StringComparison.Ordinal) && options.ContainsKey("out"))
    || (!string.Equals(command, "audit", StringComparison.Ordinal) && options.ContainsKey("device")))
{
    Console.Error.WriteLine(Usage);
    return 64;
}

Scenario scenario;
ShotList shots;

try
{
    scenario = Scenario.Load(Path.Combine(round, "scenario.json"));
    shots = ShotList.Load(Path.Combine(round, "shots.json"));

    if (shots.Against(scenario).ToList() is { Count: > 0 } unresolved)
    {
        throw new InvalidDataException($"{Path.Combine(round, "shots.json")}:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", unresolved));
    }
}
catch (Exception unreadable) when (unreadable is InvalidDataException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("parley-screens: " + unreadable.Message);
    return 65;
}

if (string.Equals(command, "check", StringComparison.Ordinal))
{
    Console.WriteLine($"{round}: {scenario.Steps.Count} steps and {shots.Shots.Count} shots read cleanly.");
    return 0;
}

var browser = options.GetValueOrDefault("browser", "msedge");

// A stylesheet laid over every page, such as a design direction's tokens, so a look can be photographed on the real
// console without being built into it.
string? overlay = null;

if (options.TryGetValue("stylesheet", out var sheet))
{
    try
    {
        overlay = await File.ReadAllTextAsync(Path.GetFullPath(sheet)).ConfigureAwait(false);
    }
    catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"parley-screens: the stylesheet could not be read: {unreadable.Message}");
        return 65;
    }
}
var output = Path.GetFullPath(options.GetValueOrDefault("out", Path.Combine(RepositoryRoot(), "artifacts", "screens", Path.GetFileName(round))));
var only = options.TryGetValue("only", out var named)
    ? named.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
    : null;

if (only is not null && only.Except(shots.Shots.Select(s => s.File), StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } unknown)
{
    Console.Error.WriteLine("parley-screens: not in shots.json: " + string.Join(", ", unknown));
    return 64;
}

// The root is made, and the process pointed at it, before anything else starts: the browser is a child process and
// inherits this environment.
var root = Deployment.CreateRoot();

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // The first Ctrl+C stops the run cleanly; a second is left to end the process, for a run that is stuck.
    e.Cancel = !stop.IsCancellationRequested;
    stop.Cancel();
};

try
{
    var camera = options.TryGetValue("device", out var device)
        ? await Camera.ConnectAsync(browser, new Uri(device)).ConfigureAwait(false)
        : await Camera.StartAsync(browser).ConfigureAwait(false);
    await using var cameraLifetime = camera.ConfigureAwait(false);
    var deployment = await Deployment.StartAsync(root, scenario.Project, scenario.Description, stop.Token).ConfigureAwait(false);
    await using var deploymentLifetime = deployment.ConfigureAwait(false);
    var seeder = new Seeder(scenario, round, deployment.BrokerAddress);
    await using var seederLifetime = seeder.ConfigureAwait(false);

    var placeholders = await seeder.PlayAsync(camera.RenderAsync, stop.Token).ConfigureAwait(false);

    // The device reaches the console at the same address it has here, through its USB link (adb reverse), so a page's
    // own links and the console's host check work unchanged. Only this port is passed back, and only for this run.
    var reversed = await Reverse.StartAsync(device is null ? 0 : new Uri(deployment.ConsoleAddress).Port).ConfigureAwait(false);
    await using var reversedLifetime = reversed.ConfigureAwait(false);

    Console.WriteLine($"Played {scenario.Steps.Count} steps into a broker at {deployment.BrokerAddress}.");

    if (string.Equals(command, "hold", StringComparison.Ordinal))
    {
        Console.WriteLine($"The console is at {deployment.ConsoleAddress}/ . Press Enter to end.");

        foreach (var shot in shots.Shots)
        {
            Console.WriteLine($"  {shot.File,-40} {deployment.ConsoleAddress}{placeholders.Expand(shot.Path)}");
        }

        var enter = Task.Run(Console.ReadLine, CancellationToken.None);
        _ = await Task.WhenAny(enter, Task.Delay(Timeout.Infinite, stop.Token).ContinueWith(_ => { }, TaskScheduler.Default)).ConfigureAwait(false);

        return stop.IsCancellationRequested ? 130 : 0;
    }

    if (string.Equals(command, "audit", StringComparison.Ordinal))
    {
        var faults = 0;

        foreach (var shot in shots.Shots.Where(s => only is null || only.Contains(s.File)))
        {
            stop.Token.ThrowIfCancellationRequested();

            try
            {
                var problems = await camera.AuditAsync(shots, shot, new Uri(deployment.ConsoleAddress + placeholders.Expand(shot.Path)), overlay).ConfigureAwait(false);
                faults += problems.Count == 0 ? 0 : 1;
                Console.WriteLine($"  {shot.File,-40} {(problems.Count == 0 ? "fits" : problems.Count.ToString(CultureInfo.InvariantCulture) + " problem(s)")}");

                foreach (var problem in problems)
                {
                    Console.WriteLine("      " + problem);
                }
            }
            catch (Exception problem) when (problem is InvalidDataException or Microsoft.Playwright.PlaywrightException or TimeoutException)
            {
                faults++;
                Console.Error.WriteLine($"  {shot.File,-40} FAILED: {problem.Message.Split('\n')[0]}");
            }
        }

        return faults == 0 ? 0 : 1;
    }

    Directory.CreateDirectory(output);

    // With --only, the other shots' lines are kept, so sha256.txt always describes every PNG the folder holds.
    var hashes = Manifest(Path.Combine(output, "sha256.txt"));
    var failed = 0;

    foreach (var shot in shots.Shots.Where(s => only is null || only.Contains(s.File)))
    {
        var target = Path.Combine(output, shot.File);

        try
        {
            var png = await camera.TakeAsync(shots, shot, new Uri(deployment.ConsoleAddress + placeholders.Expand(shot.Path)), overlay).ConfigureAwait(false);
            var hash = Camera.Sha256(png);

            await File.WriteAllBytesAsync(target, png, stop.Token).ConfigureAwait(false);
            hashes[shot.File] = hash;
            Console.WriteLine($"  {shot.File,-40} {Camera.Kilobytes(png.Length),9}  {hash}");
        }
        catch (Exception problem) when (problem is InvalidDataException or Microsoft.Playwright.PlaywrightException or TimeoutException)
        {
            // A PNG left from an earlier run would be mistaken for this one's.
            failed++;
            File.Delete(target);
            hashes.Remove(shot.File);
            Console.Error.WriteLine($"  {shot.File,-40} FAILED: {problem.Message.Split('\n')[0]}");
        }
    }

    var listed = new StringBuilder();

    foreach (var shot in shots.Shots.Where(s => hashes.ContainsKey(s.File)))
    {
        _ = listed.Append(CultureInfo.InvariantCulture, $"{shot.File} {hashes[shot.File]}\n");
    }

    await File.WriteAllTextAsync(Path.Combine(output, "sha256.txt"), listed.ToString(), stop.Token).ConfigureAwait(false);
    Console.WriteLine(failed == 0 ? $"Wrote {output}" : $"Wrote {output}, {failed} shot(s) failed");

    return failed == 0 ? 0 : 1;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    return 130;
}
catch (Exception refused) when (refused is InvalidOperationException or Grpc.Core.RpcException)
{
    Console.Error.WriteLine("parley-screens: " + refused.Message);
    return 70;
}
catch (Microsoft.Playwright.PlaywrightException browserProblem)
{
    Console.Error.WriteLine($"parley-screens: the browser '{browser}' could not be used: {browserProblem.Message.Split('\n')[0]}");
    return 69;
}
finally
{
    // Normally the deployment removes it; this covers a run that ended before the deployment existed.
    if (Directory.Exists(root))
    {
        Deployment.Remove(root);
    }
}

static Dictionary<string, string>? Options(string[] rest)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);

    for (var i = 0; i < rest.Length; i += 2)
    {
        if (i + 1 >= rest.Length || rest[i] is not ("--out" or "--only" or "--browser" or "--stylesheet" or "--device"))
        {
            return null;
        }

        options[rest[i][2..]] = rest[i + 1];
    }

    return options;
}

static Dictionary<string, string> Manifest(string path)
{
    var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    if (File.Exists(path))
    {
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Split(' ') is [var name, var hash])
            {
                hashes[name] = hash;
            }
        }
    }

    return hashes;
}

static string RepositoryRoot()
{
    // The build output sits under the repository; artifacts/ there is ignored by git. Run from elsewhere, the current
    // folder stands in.
    for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
    {
        if (File.Exists(Path.Combine(folder.FullName, "Parley.slnx")))
        {
            return folder.FullName;
        }
    }

    return Directory.GetCurrentDirectory();
}
