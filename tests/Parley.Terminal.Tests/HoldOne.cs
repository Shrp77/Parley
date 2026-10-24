namespace Parley.Terminal.Tests;

/// <summary>A slow broker for one call: holds the first call to a path after <see cref="Arm"/> until <see cref="Release"/>.</summary>
/// <remarks>
/// For reads the shell starts off its work queue, which <c>Settled</c> does not wait for: a test can then act while the
/// read is still out and see what it does when it lands.
/// </remarks>
/// <param name="path">The end of the call's path, for example <c>/Roadmaps</c>.</param>
internal sealed class HoldOne(string path) : DelegatingHandler(new SocketsHttpHandler())
{
    private readonly SemaphoreSlim _go = new(0, 1);
    private int _armed;

    /// <summary>Set once the held call has reached the broker's door.</summary>
    internal ManualResetEventSlim Holding { get; } = new(false);

    /// <summary>Holds the next matching call.</summary>
    internal void Arm() => Volatile.Write(ref _armed, 1);

    /// <summary>Lets the held call through.</summary>
    internal void Release() => _go.Release();

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.Ordinal)
            && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
        {
            Holding.Set();
            await _go.WaitAsync(cancellationToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _go.Dispose();
            Holding.Dispose();
        }

        base.Dispose(disposing);
    }
}
