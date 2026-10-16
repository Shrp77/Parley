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

var listenOn = TakeOption("--listen", "-l");
var brokerAddress = TakeOption("--broker", "-b");

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

try
{
    await ConsoleHost.RunAsync([], address, Console.Error, brokerAddress).ConfigureAwait(false);

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
