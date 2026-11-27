using System.Diagnostics;
using System.Globalization;

namespace Parley.Screens;

/// <summary>A port on a USB-connected device passed back to the same port on this machine, for one run.</summary>
/// <remarks>
/// <c>adb reverse</c> makes the device's own <c>127.0.0.1:port</c> this machine's, so the device's browser opens the
/// console at the address the console already answers to, and nothing is bound beyond loopback. The device is the one
/// adb picks: the only one connected, or the one <c>ANDROID_SERIAL</c> names. The mapping is removed when the run ends.
/// </remarks>
internal sealed class Reverse : IAsyncDisposable
{
    private readonly int _port;

    private Reverse(int port) => _port = port;

    /// <summary>Passes a port back, or does nothing for port 0.</summary>
    /// <param name="port">The console's port here, or 0 when no device is used.</param>
    /// <returns>What removes it again.</returns>
    /// <exception cref="InvalidOperationException">adb could not be run or refused.</exception>
    public static async Task<Reverse> StartAsync(int port)
    {
        if (port != 0)
        {
            var spec = "tcp:" + port.ToString(CultureInfo.InvariantCulture);
            await AdbAsync("reverse", spec, spec).ConfigureAwait(false);
        }

        return new Reverse(port);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_port != 0)
        {
            try
            {
                await AdbAsync("reverse", "--remove", "tcp:" + _port.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // A device unplugged mid-run has dropped the mapping with it.
            }
        }
    }

    private static async Task AdbAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("adb") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var adb = Process.Start(start) ?? throw new InvalidOperationException("adb did not start");
            var error = await adb.StandardError.ReadToEndAsync().ConfigureAwait(false);
            _ = await adb.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await adb.WaitForExitAsync().ConfigureAwait(false);

            if (adb.ExitCode != 0)
            {
                throw new InvalidOperationException($"adb {string.Join(' ', arguments)} failed: {error.Trim()}");
            }
        }
        catch (System.ComponentModel.Win32Exception missing)
        {
            throw new InvalidOperationException("adb could not be run; is the Android platform-tools folder on PATH? " + missing.Message, missing);
        }
    }
}
