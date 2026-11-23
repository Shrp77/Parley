using System.Globalization;
using System.Reflection;
using Parley.Configuration;
using Parley.Core;
using Parley.Web;

// `parley-web-ui`: the operator's web UI, started directly, by `parley web-ui`, or by a broker
// running with `--with-web-ui`.
//
// **It parses its own arguments rather than being handed them parsed.** The wrapper in `parley`
// passes what it was given through untouched, so there is one place that decides what `--listen`
// means. A wrapper that understood the arguments would be a second parser to keep in step, and the
// whole reason this is a separate tool is that the two are versioned and installed apart.

const string Help = """
parley-web-ui: the operator's web UI.

  parley-web-ui [<url>] [--listen <url>] [--broker <address>]

  <url>, --listen     Where to listen. Defaults to http://127.0.0.1:5280.
  --broker <address>  A broker to offer when this machine's manifest names no project.
                      PARLEY_BROKER says the same; the flag wins.
  --until-input-closes
                      Stop when standard input closes. A broker passes it with a pipe, so
                      the web UI ends with the broker even when the broker is killed.
                      With it, one line on standard output says when it is serving.
  --version, -v       The build and the protocol it speaks.
  --help, -h          This.

Started by `parley web-ui` as well, which forwards here and parses nothing, and by a broker
running with --with-web-ui, which starts it as a child and does not wait for it.
""";

var arguments = new List<string>(args);

if (arguments.Exists(argument => argument is "--help" or "-h"))
{
    await Console.Out.WriteLineAsync(Help).ConfigureAwait(false);

    return 0;
}

if (arguments.Exists(argument => argument is "--version" or "-v"))
{
    // Two numbers for the reason `parley --version` gives two: this tool and the broker are
    // installed apart, so they can genuinely differ, and §10.2 is a versioned contract.
    var build = typeof(ConsoleHost).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    var plus = build.IndexOf('+', StringComparison.Ordinal);

    await Console.Out.WriteLineAsync(string.Create(
        CultureInfo.InvariantCulture,
        $"parley-web-ui {(plus < 0 ? build : build[..plus])}, "
        + $"speaking Parley protocol {ProtocolVersion.Current}")).ConfigureAwait(false);

    return 0;
}

string? missingValue = null;

// Taken before the unknown-option check, because it has no value to take with it.
var untilInputCloses = arguments.Remove("--until-input-closes");

var listenOn = TakeOption("--listen", "-l");
// PARLEY_BROKER says the same as --broker, as it does to the terminal and to `parley`; the flag wins.
var brokerAddress = TakeOption("--broker", "-b")
    ?? (Environment.GetEnvironmentVariable(BrokerEndpoint.Variable) is { Length: > 0 } named ? named : null);

if (missingValue is not null)
{
    await Console.Error.WriteLineAsync($"{missingValue} needs a value.").ConfigureAwait(false);
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);

    return 64;
}

if (arguments.Find(argument => argument.StartsWith('-')) is { } unknown)
{
    await Console.Error.WriteLineAsync($"parley-web-ui does not take {unknown}.").ConfigureAwait(false);
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);

    return 64;
}

// A bare address is accepted as well as --listen, and `parley web-ui <url>` forwards one.
var address = listenOn ?? (arguments.Count > 0 ? arguments[0] : null);

if (arguments.Count > 1)
{
    await Console.Error.WriteLineAsync(
        $"parley-web-ui takes one address, and was given {arguments.Count}.").ConfigureAwait(false);
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);

    return 64;
}

// Checked here rather than left to Kestrel, which refuses a malformed address several frames deep
// in a stack trace that says nothing about what was typed.
if (address is not null && !Uri.TryCreate(address, UriKind.Absolute, out _))
{
    await Console.Error.WriteLineAsync(
        $"'{address}' is not an address the web UI can bind. Give it one like "
        + $"{WebUiAddress.Loopback}.").ConfigureAwait(false);

    return 64;
}

await Console.Out.WriteLineAsync(
    $"Parley web UI on {address ?? WebUiAddress.Loopback}. Ctrl+C stops it.").ConfigureAwait(false);

// **Ends with the broker that started it, however that broker ended.** The broker holds the other end of this
// process's standard input and never writes to it. When the broker stops in order it closes the pipe; when it is
// killed outright the operating system closes it. Either way the read below returns end of input and the web UI
// stops, rather than holding its port with the settings of a broker that is gone, which is what made the next
// broker's web UI fail to bind. Only on the flag: a web UI run by hand reads nothing from its terminal.
using var parentGone = new CancellationTokenSource();

if (untilInputCloses)
{
    _ = Task.Run(
        async () =>
        {
            try
            {
                var input = Console.OpenStandardInput();

                await using (input.ConfigureAwait(false))
                {
                    var buffer = new byte[256];

                    while (await input.ReadAsync(buffer).ConfigureAwait(false) > 0)
                    {
                        // Nothing is written on purpose; anything that is, is ignored.
                    }
                }
            }
            catch (IOException)
            {
                // A broken pipe is the parent gone too.
            }

            try
            {
                await parentGone.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The web UI already stopped for another reason.
            }
        },
        CancellationToken.None);
}

try
{
    // **A broker is told when this is serving, and only a broker.** It waits for this line before it names the address
    // to a person: a connection accepted at the address could be another program's. Written by hand it would be noise,
    // so only on the flag a broker passes.
    await ConsoleHost.RunAsync(
        [],
        address,
        Console.Error,
        brokerAddress,
        untilInputCloses
            ? async bound =>
            {
                await Console.Out.WriteLineAsync(WebUiAddress.ServingMarker + string.Join(';', bound))
                    .ConfigureAwait(false);
                await Console.Out.FlushAsync().ConfigureAwait(false);
            }
            : null,
        parentGone.Token).ConfigureAwait(false);

    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception failure) when (failure is not OutOfMemoryException)
{
    // Reported as a sentence rather than a stack trace, because the two things that put somebody
    // here are a port already held and an address they cannot bind — both of which they can act
    // on, and neither of which is a defect in this program.
    //
    // Not OutOfMemoryException, matching the one other catch-all in this repository: a process out
    // of memory is not contained by carrying on, and swallowing it here would leave a host running
    // in a state nothing else expects to survive.
    await Console.Error.WriteLineAsync(
        $"The web UI did not start on {address ?? WebUiAddress.Loopback}: {failure.Message}")
        .ConfigureAwait(false);

    return 69;
}

// An option with no value is its own answer, not "no such option". `parley-web-ui --listen` must
// not be refused with "does not take --listen", which is false and sends somebody to check a
// spelling that was correct.
string? TakeOption(string name, string? shortName = null)
{
    for (var index = 0; index < arguments.Count; index++)
    {
        var argument = arguments[index];

        if (argument != name && argument != shortName)
        {
            continue;
        }

        if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith('-'))
        {
            missingValue = argument;
            arguments.RemoveAt(index);

            return null;
        }

        var value = arguments[index + 1];

        arguments.RemoveRange(index, 2);

        return value;
    }

    return null;
}
