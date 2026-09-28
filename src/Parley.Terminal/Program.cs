using System.Globalization;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Parley.Terminal;

// `parley-terminal`: the operator's terminal surface, started directly or forwarded to by `parley terminal`.
//
// **It parses its own arguments rather than being handed them parsed, and that is the point of
// the split.** The forwarding wrapper in `parley` passes what it was given through untouched, so
// there is exactly one place that decides what `--config` means here. A wrapper that understood
// the arguments would be a second parser to keep in step, and the whole reason this is a separate
// tool is that the two are versioned and installed apart.
//
// The four it takes are the four `parley top` took before the forwarding: where the manifest is, a broker to offer when
// the manifest names none, and the two that answer a question and exit.

// The manual, kept short because this surface explains itself once it is open: everything a person
// can do from it is on the screen, and the two arguments here are only about which deployment it
// opens on.
const string Help = """
parley-terminal: the operator's terminal surface.

  parley-terminal [--config <path>] [--broker <address>]

  --config <path>     The project manifest to read. Defaults to this machine's.
  --broker <address>  A broker to offer when the manifest names no project.
  --version, -v       The build and the protocol it speaks.
  --help, -h          This.

Started by `parley` on its own as well, which forwards here and parses nothing.
""";

var arguments = new List<string>(args);

if (arguments.Exists(argument => argument is "--help" or "-h"))
{
    await Console.Out.WriteLineAsync(Help).ConfigureAwait(false);

    return 0;
}

if (arguments.Exists(argument => argument is "--version" or "-v"))
{
    // Two numbers for the reason `parley --version` gives two: the package version says which
    // build is on PATH, and the protocol version says what this speaks. They are more likely to
    // differ here than they ever were, because this tool and the broker are now installed apart —
    // which is the point of the split and the thing somebody diagnosing a deployment needs to see.
    await Console.Out.WriteLineAsync(string.Create(
        CultureInfo.InvariantCulture,
        $"parley-terminal {BuildIdentity.Version}, speaking Parley protocol {ProtocolVersion.Current}"))
        .ConfigureAwait(false);

    return 0;
}

// Set by TakeOption when a flag it knows was given without one.
string? missingValue = null;

var configPath = TakeOption("--config");
var brokerAddress = TakeOption("--broker", "-b");

// Taken and warned about rather than refused, because `parley top --project x` used to say this
// and forwarding must not turn a warning into a failure. It is not scoped to one project: the
// first screen is the list of them, and it re-reads the manifest while it runs, so a project named
// elsewhere appears without restarting it.
if (TakeOption("--project", "-p") is { } named)
{
    await Console.Error.WriteLineAsync(
        $"parley-terminal is not scoped to one project, so --project {named} was not used. "
        + "Every project this machine knows is on its first screen.").ConfigureAwait(false);
}

if (missingValue is not null)
{
    await Console.Error.WriteLineAsync($"{missingValue} needs a value.").ConfigureAwait(false);
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);

    return 64;
}

// Refused rather than ignored. An option this does not have is far more likely to be a typo for
// one it does than a thing somebody meant, and a surface that silently discards what it was asked
// for is the one that gets blamed for not doing it.
if (arguments.Find(argument => argument.StartsWith('-')) is { } unknown)
{
    await Console.Error.WriteLineAsync($"parley-terminal does not take {unknown}.").ConfigureAwait(false);
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);

    return 64;
}

if (arguments.Count > 0)
{
    await Console.Error.WriteLineAsync(
        $"parley-terminal takes no arguments of its own, and was given {arguments[0]}.")
        .ConfigureAwait(false);
    await Console.Error.WriteLineAsync(Help).ConfigureAwait(false);

    return 64;
}

string manifestPath;

try
{
    manifestPath = ProjectManifest.Locate(configPath, Environment.GetEnvironmentVariable);
}
catch (InvalidOperationException noHome)
{
    // A machine with no per-user configuration directory at all. Reported rather than thrown, for
    // the reason `parley` reports it: the alternative to refusing is a manifest resolved against
    // the working directory, which is a surface showing a deployment nobody named.
    await Console.Error.WriteLineAsync(noHome.Message).ConfigureAwait(false);

    return 78;
}

// The same fallback the web UI offers, so the two surfaces list the same deployments on a machine
// that has named none. A person who can reach a broker from `parley registry` and not from here
// would reasonably conclude this surface was broken.
//
// **Only when somebody said where, which is the half this was missing.** `Endpoint` answers with a
// built-in default when nothing names a broker, and this passed that on regardless — so a machine
// that has simply not named its first project yet was handed a made-up address to go and ask. The
// screen does not draw until every broker has answered, and nothing is listening on a made-up
// address, so the first thing a person saw after typing `parley` was two seconds of nothing: the
// `ConnectTimeout` in `BrokerDirectory.Connect`, spent on an address no one chose. Worst during
// setup, which is when `parley` is typed most.
//
// A deployment configured entirely by explicit paths still works, which is what the fallback is
// for and what SPEC.md §4.1 permits: --broker and PARLEY_BROKER are exactly the ways of saying so,
// and both are kept. What is dropped is the guess.
var endpoint = ProjectResolution.Endpoint(brokerAddress, Environment.GetEnvironmentVariable, null);

var fallback = endpoint.Source == ValueSource.Default ? null : endpoint.Value;

return await TerminalCommand.RunAsync(manifestPath, fallback).ConfigureAwait(false);

// **An option with no value is its own answer, not "no such option".** The first draft returned
// null and left the flag in the list, so `parley-top --config` was refused with "parley-top does
// not take --config" -- which is false: it does take it, and what was missing was the path. A
// refusal that misnames the problem sends somebody to check the spelling of an option that was
// spelled correctly.
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
