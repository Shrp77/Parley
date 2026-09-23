using Grpc.Net.Client;
using Parley.Client;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That everything this surface holds is let go once, and lets go cleanly when it is torn down
/// under something that is still running.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.7. Two things here outlive a keystroke: the follower holding a
/// log file open, and the chain of reads a keystroke queued. Both are torn down by a person
/// quitting, which happens at an arbitrary moment — so the question is not whether the tidy path
/// works but what the untidy one leaves behind.
/// </para>
/// <para>
/// An unobserved exception on a background task is how two probes in this repository hung for ten
/// minutes instead of failing, so a follower abandoned mid-read is a shape worth pinning.
/// </para>
/// </remarks>
public sealed class LifetimeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-lifetime-" + Path.GetRandomFileName());

    public LifetimeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    [Fact]
    public async Task Stopping_a_follower_twice_is_not_an_error()
    {
        // Escape and then quitting, which is the ordinary order and reaches the same code twice.
        var path = Path.Combine(_directory, LogTail.FileName);

        await File.WriteAllTextAsync(path, "started\n", TestContext.Current.CancellationToken);

        var view = new LogView("following");

        view.Start(path, string.Empty);

        await view.StopAsync();
        await view.StopAsync();
    }

    [Fact]
    public async Task A_follower_that_is_disposed_without_being_stopped_lets_the_file_go()
    {
        // What quitting does: the view is disposed by the application coming down, and nothing
        // called Stop. A follower left holding the file would keep a lock on a broker's log after
        // the terminal had gone.
        var path = Path.Combine(_directory, LogTail.FileName);

        await File.WriteAllTextAsync(path, "started\n", TestContext.Current.CancellationToken);

        var view = new LogView("following");

        view.Start(path, string.Empty);

        view.Dispose();

        // Deleting is the strongest test that nothing holds it: on Windows a held file cannot be
        // removed, which is the platform this matters on.
        await Eventually.UntilAsync(
            () =>
            {
                try
                {
                    File.Delete(path);

                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            },
            "the log was still held after the view was disposed",
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_log_deleted_under_a_follower_does_not_end_it_in_an_exception()
    {
        // Rotation, in its most abrupt form. The follower reopens rather than faulting, and a
        // follower that faulted here would leave the screen saying "Following ..." for ever.
        var path = Path.Combine(_directory, LogTail.FileName);

        await File.WriteAllTextAsync(path, "started\n", TestContext.Current.CancellationToken);

        var view = new LogView("following");

        view.Start(path, string.Empty);

        File.Delete(path);

        await Task.Delay(300, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "and again\n", TestContext.Current.CancellationToken);

        await view.StopAsync();
    }

    [Fact]
    public async Task A_surface_disposed_with_reads_in_flight_comes_down_without_faulting()
    {
        // Ctrl+Q while three regions are still waiting on brokers. The reads observe the shell's
        // own token, so what happens is a cancellation rather than an exception nobody catches.
        using var driving = Driving.Open();
        using var directory = Directory_();

        var shell = new Shell(directory);

        driving.Show(shell);

        shell.Queued(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
        });

        shell.Dispose();

        // Completes rather than faulting: the surface going away is not news.
        await shell.Settled;
    }

    [Fact]
    public void Disposing_a_surface_twice_is_not_an_error()
    {
        using var driving = Driving.Open();
        using var directory = Directory_();

        var shell = new Shell(directory);

        driving.Show(shell);

        shell.Dispose();
        shell.Dispose();
    }

    private static BrokerDirectory Directory_() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
