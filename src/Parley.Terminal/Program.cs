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
// What it takes: where the manifest is, a broker to offer when the manifest names none, and the two options that
// answer a question and exit. It is not scoped to one project: the first screen lists them all.

// The manual, kept short because this surface explains itself once it is open: everything a person
// can do from it is on the screen, and the two arguments here are only about which deployment it
// opens on.
const string Help = """
parley-terminal: the operator's terminal surface.

  parley-terminal [--config <path>] [--broker <address>]

  --config <path>     The folder that holds this machine's projects/ folder.
                      Defaults to this machine's.
  --broker <address>  A broker to offer when that folder holds no project.
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
    // build is on PATH, and the protocol version says what this speaks. They can differ, because this
    // tool and the broker are installed apart.
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
// **Only when somebody said where.** `Endpoint` answers with a built-in default when nothing names a
// broker, and a made-up address would cost the `ConnectTimeout` in `BrokerDirectory.Connect` on an
// address no one chose, worst during setup.
//
// A broker reached by address works, which is what the fallback is for (watching a broker that runs
// elsewhere, through a tunnel): --broker and PARLEY_BROKER are the ways of saying so. The default is
// not a guess to offer.
var endpoint = ProjectResolution.Endpoint(brokerAddress, Environment.GetEnvironmentVariable, null);

var fallback = endpoint.Source == ValueSource.Default ? null : endpoint.Value;

return await TerminalCommand.RunAsync(manifestPath, fallback).ConfigureAwait(false);

// **An option with no value is its own answer, not "no such option".** A refusal that misnames the
// problem sends somebody to check the spelling of an option that was spelled correctly.
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
