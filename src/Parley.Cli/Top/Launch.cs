using System.Diagnostics;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// Handing off to the web console, for when a bigger screen is the better reader.
/// </summary>
/// <remarks>
/// FR-014, and it assumes the console is worth opening — which it is. Nothing here replaces it:
/// on a large screen it remains the better transcript, and this surface exists because a browser
/// is a thing a machine may not have and a person may not be at, not because the console was
/// wrong.
/// </remarks>
internal static class Launch
{
    /// <summary>Opens an address in whatever the platform considers the default browser.</summary>
    /// <param name="console">Where to report what happened.</param>
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
    internal static bool Open(IAnsiConsole console, string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out _))
        {
            console.MarkupLine($"[red]'{ProjectList.Escape(address)}' is not an address.[/]");

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

            console.MarkupLine($"[green]Opened[/] [dim]{ProjectList.Escape(address)}[/]");

            return true;
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception
                                                or InvalidOperationException
                                                or PlatformNotSupportedException)
        {
            // Named rather than swallowed, and the address is printed so somebody on a headless
            // machine can copy it to a browser that is not on this one.
            console.MarkupLine(
                $"[yellow]No browser could be started here.[/] Open "
                + $"[bold]{ProjectList.Escape(address)}[/] yourself. "
                + $"[dim]({ProjectList.Escape(failure.Message)})[/]");

            return false;
        }
    }
}
