using System.Diagnostics;

namespace Parley.Setup;

/// <summary>
/// Handing off to the web console, for when a bigger screen is the better reader.
/// </summary>
/// <remarks>
/// FR-014, and it assumes the console is worth opening — which it is. Nothing here replaces it:
/// on a large screen it remains the better transcript, and this surface exists because a browser
/// is a thing a machine may not have and a person may not be at, not because the console was
/// wrong.
/// </remarks>
public static class Launch
{
    /// <summary>Opens an address in whatever the platform considers the default browser.</summary>
    /// <param name="said">Told what happened.</param>
    /// <param name="address">What to open.</param>
    /// <returns><see langword="true"/> when the platform was asked to open it.</returns>
    /// <remarks>
    /// <para>
    /// Three platforms, three launchers, and no attempt to be clever about it. On Windows the
    /// shell is asked to open the URL by setting <c>UseShellExecute</c>, which is what makes a
    /// URL a thing the machine knows how to handle; on macOS and Linux that is <c>open</c> and
    /// <c>xdg-open</c>.
    /// </para>
    /// <para>
    /// <b>A failure here is reported and never thrown.</b> There is no browser on a headless
    /// machine and no <c>xdg-open</c> on a minimal container, and both are ordinary places to run
    /// this. FR-014 says the surface keeps running when it opens a browser; it had better keep
    /// running when it cannot.
    /// </para>
    /// </remarks>
    public static bool Open(Action<string> said, string address)
    {
        if (!IsWebAddress(address))
        {
            said($"'{address}' is not an http or https address.");

            return false;
        }

        try
        {
            var started = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(address) { UseShellExecute = true }
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", address)
                    : new ProcessStartInfo("xdg-open", address);

            using var process = Process.Start(started);

            said($"Opened {address}");

            return true;
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception
                                                or InvalidOperationException
                                                or PlatformNotSupportedException)
        {
            // Named rather than swallowed, and the address is printed so somebody on a headless
            // machine can copy it to a browser that is not on this one.
            said(
                $"No browser could be started here. Open {address} yourself. ({failure.Message})");

            return false;
        }
    }

    /// <summary>Whether an address is one this is willing to hand to the platform.</summary>
    /// <param name="address">The address.</param>
    /// <returns><see langword="true"/> when it is an absolute http or https URL.</returns>
    /// <remarks>
    /// <para>
    /// Web schemes only, and that is narrower than "is it a URI" on purpose. On Windows
    /// <see cref="Open"/> hands the string to the shell, which launches whatever handler the
    /// scheme is registered to — so <c>file:</c> would open a file and a custom scheme would run
    /// whatever claimed it. The only caller today passes a constant, which makes this a guard
    /// against the second caller rather than the first: the moment somebody opens an address that
    /// came from a manifest or a broker, the difference between "is a URI" and "is a web page" is
    /// the whole of the protection.
    /// </para>
    /// <para>
    /// <b>Separate from <see cref="Open"/> so that proving the guard lets something through does
    /// not start a browser.</b> The test for that used to call <c>Open</c> with a loopback URL and
    /// assert no refusal was printed, on the reasoning that whether a browser starts is the
    /// platform's business and there is none on a CI runner. That is true on a CI runner and false
    /// on the machine of whoever is working on this: it opened a tab on every run of the suite.
    /// The predicate is the part worth asserting, and it launches nothing.
    /// </para>
    /// </remarks>
    public static bool IsWebAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    /// <summary>Whether something accepts connections at the host and port of a web address.</summary>
    /// <param name="address">A web address, as <see cref="IsWebAddress"/> accepts.</param>
    /// <returns><see langword="true"/> when a connection was made within two seconds.</returns>
    /// <remarks>
    /// Asked before a browser is handed the address, because a browser answers a port nobody holds
    /// with an error page, and from <see cref="Open"/>'s side that is still success. The web UI is
    /// a child its broker neither waits on nor restarts, so it can be gone while the broker serves.
    /// </remarks>
    public static bool IsListening(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        try
        {
            using var client = new System.Net.Sockets.TcpClient();

            return client.ConnectAsync(parsed.Host, parsed.Port).Wait(TimeSpan.FromSeconds(2))
                && client.Connected;
        }
        catch (Exception failure) when (failure is AggregateException
                                                or System.Net.Sockets.SocketException
                                                or InvalidOperationException)
        {
            return false;
        }
    }
}
