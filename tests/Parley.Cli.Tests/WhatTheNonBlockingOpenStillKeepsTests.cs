using System.Net.Sockets;
using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the open which refuses a pipe off Windows still does what the ordinary open did for everything else: a socket is
/// named as what it is, a folder where the file should be reads as no file, and a file held for rewriting is not read
/// meanwhile (SPEC.md §4.1; the remarks on <see cref="LocalOpen"/>).
/// </summary>
/// <remarks>
/// Review, lens 3 on fc26cb8a, mutations run in a Linux container: the socket's sentence, the folder's reading as no file
/// and the advisory lock each passed every test with them removed. The folder and the lock cases hold on Windows too,
/// where .NET's own open answers them; the socket is skipped there.
/// </remarks>
[Integration]
public sealed class WhatTheNonBlockingOpenStillKeepsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("parley-open-keeps-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void A_launch_configuration_that_is_a_socket_is_refused_as_not_an_ordinary_file()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A socket at a path in a folder is refused there by its own open.");

        var workspace = _root.CreateSubdirectory("orders").FullName;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(Path.Combine(workspace, LaunchConfiguration.FileName)));

        Assert.False(LaunchConfiguration.TryRead(workspace, out _, out var problem));
        Assert.Contains("not an ordinary file", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_where_the_launch_configuration_goes_reads_as_no_file()
    {
        var workspace = _root.CreateSubdirectory("orders").FullName;
        _ = Directory.CreateDirectory(Path.Combine(workspace, LaunchConfiguration.FileName));

        Assert.True(LaunchConfiguration.TryRead(workspace, out _, out var problem), problem);
        Assert.Equal((LocalOpenStatus.Missing, null), LocalOpen.File(
            Path.Combine(workspace, LaunchConfiguration.FileName), FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
    }

    [Theory]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    [InlineData(FileShare.None)]
    public void A_file_held_for_rewriting_is_not_opened_beside_it(FileShare second)
    {
        var path = Path.Combine(_root.FullName, LaunchConfiguration.FileName);
        File.WriteAllText(path, "{}");

        var (held, stream) = LocalOpen.File(path, FileAccess.ReadWrite, FileShare.None);

        using (stream)
        {
            Assert.Equal(LocalOpenStatus.Opened, held);

            var (beside, other) = LocalOpen.File(path, FileAccess.Read, second);

            other?.Dispose();

            Assert.Equal(LocalOpenStatus.Busy, beside);
        }
    }
}
