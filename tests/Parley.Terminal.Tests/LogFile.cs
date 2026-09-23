namespace Parley.Terminal.Tests;

/// <summary>
/// A broker's log on disk, for tests that follow one.
/// </summary>
/// <remarks>
/// The share flags matter and are the reason this is a type rather than a couple of helper
/// methods: a writer that opened without <c>FileShare.ReadWrite | FileShare.Delete</c> would fail
/// against a running follower, and a test failing because its *own* writer took a lock is a test
/// that looks like a product bug. Written once, correctly, in the same shape the broker uses.
/// </remarks>
internal sealed class LogFile : IDisposable
{
    private readonly string _directory;

    /// <summary>Creates a directory with a log in it.</summary>
    /// <param name="tag">Something to recognise the directory by while it exists.</param>
    internal LogFile(string tag)
    {
        _directory = Path.Combine(Path.GetTempPath(), $"parley-{tag}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_directory);

        At = Path.Combine(_directory, "broker.log");

        File.WriteAllText(At, "already here\n");
    }

    /// <summary>Where the log is.</summary>
    /// <remarks>
    /// Not called <c>Path</c>: a property of that name shadows <see cref="System.IO.Path"/> inside
    /// this type, so the constructor above stops being able to call <c>Path.Combine</c>.
    /// </remarks>
    internal string At { get; }

    /// <summary>Appends one line, the way a broker writing its log would.</summary>
    /// <param name="line">What to write.</param>
    /// <returns>When it is on disk.</returns>
    internal async Task AppendAsync(string line)
    {
        await using var writer = new FileStream(
            At, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        await using var text = new StreamWriter(writer);

        await text.WriteLineAsync(line);
    }

    /// <summary>Empties the file in place, the way a rotating writer would.</summary>
    internal void Truncate()
    {
        using var file = new FileStream(
            At, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A follower on a worker can still hold the handle when a test ends. The temp
            // directory is the operating system's problem at that point, not this suite's.
        }
    }
}
