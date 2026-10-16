using Parley.Configuration;
using Parley.Core;
using Xunit;
using static Parley.Cli.Tests.Loopback;

namespace Parley.Cli.Tests;

/// <summary>
/// That a serving broker is not stopped by its project.json being caught mid-edit or mid-replace.
/// </summary>
/// <remarks>
/// Reading leaves an unreadable project out, so the watcher must not take that as the project being removed, and
/// since it stores the stamp before reading, a read that collides with <c>File.Replace</c> must not leave the broker
/// stopped for good. Driven through the real serve loop and
/// <see cref="ServeCommand.Rereader"/>, the function the program itself uses.
/// </remarks>
public sealed class AHalfEditedProjectKeepsServingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-half-edited-" + Guid.NewGuid().ToString("N"));

    public AHalfEditedProjectKeepsServingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_half_written_configuration_does_not_stop_a_serving_broker()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = ProjectName.Create("orders");
        var orders = new ProjectSettings(name, ProjectFolder.StoreFor(_root, name), $"http://127.0.0.1:{FreePort()}", null);
        ProjectFolder.Write(_root, orders);

        Assert.True(ProjectManifest.TryRead(_root, out var initial, out _));

        var reread = ServeCommand.Rereader(_root);

        var output = new StringWriter();
        var error = new StringWriter();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(initial, null, output, error, reread: reread, cancellationToken: stopping.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct), "never came up");

            // An editor that truncates and then writes: the first half lands.
            var file = ProjectFolder.ConfigurationFor(_root, name);
            var whole = await File.ReadAllTextAsync(file, ct);
            await File.WriteAllTextAsync(file, whole[..(whole.Length / 2)], ct);

            await Task.Delay(ServeCommand.WatchInterval * 2 + TimeSpan.FromMilliseconds(500), ct);

            var upWhileHalf = await AnswersAsync(orders.Endpoint, ct, attempts: 3);

            // The rest lands.
            await File.WriteAllTextAsync(file, whole, ct);
            await Task.Delay(ServeCommand.WatchInterval * 2 + TimeSpan.FromMilliseconds(500), ct);
            var upAfter = await AnswersAsync(orders.Endpoint, ct);
            Assert.True(upAfter, "the broker did not come back once the file was whole:\n" + output);
            Assert.True(upWhileHalf, "a half-written project.json stopped the serving broker:\n" + output);
        }
        finally
        {
            await stopping.CancelAsync();
        }

        _ = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    [Fact]
    public async Task A_read_that_collides_with_a_write_does_not_leave_the_project_stopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = ProjectName.Create("orders");
        var orders = new ProjectSettings(name, ProjectFolder.StoreFor(_root, name), $"http://127.0.0.1:{FreePort()}", null);
        ProjectFolder.Write(_root, orders);

        Assert.True(ProjectManifest.TryRead(_root, out var initial, out _));

        var reread = ServeCommand.Rereader(_root);

        var output = new StringWriter();
        var error = new StringWriter();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(initial, null, output, error, reread: reread, cancellationToken: stopping.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct), "never came up");

            // What File.Replace holds while it commits a write: the file, with no read sharing, and a new write time.
            var file = ProjectFolder.ConfigurationFor(_root, name);
            using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                File.SetLastWriteTimeUtc(held.SafeFileHandle, DateTime.UtcNow);
                await Task.Delay(ServeCommand.WatchInterval * 2 + TimeSpan.FromMilliseconds(500), ct);
            }

            // The write is over; the file is whole and unchanged. Give the watcher several ticks.
            await Task.Delay(ServeCommand.WatchInterval * 4, ct);
            var upAfter = await AnswersAsync(orders.Endpoint, ct, attempts: 3);

                        Assert.True(upAfter, "the project stayed stopped after the write finished: " + output);
        }
        finally
        {
            await stopping.CancelAsync();
        }

        _ = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }
}
