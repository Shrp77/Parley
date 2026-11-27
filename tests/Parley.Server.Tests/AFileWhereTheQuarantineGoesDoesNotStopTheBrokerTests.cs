using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// That something other than a folder where the quarantine goes does not stop the file location being prepared when a
/// broker starts (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// Found by review, lens 1 run 93. Preparing tightens a quarantine an older broker left, and a file another program put
/// there made it throw at start; a move into quarantine is what refuses it, as before.
/// </remarks>
[Integration]
public sealed class AFileWhereTheQuarantineGoesDoesNotStopTheBrokerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-quarantine-file-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        File.Delete(_root + "-quarantine");

        foreach (var path in new[] { _root, _root + "-incoming" })
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void A_file_at_the_quarantine_path_is_left_alone_and_the_location_is_prepared()
    {
        File.WriteAllText(_root + "-quarantine", "somebody else's");

        using var location = new FileLocationStore(_root, _root + "-incoming");
        location.Prepare();

        Assert.True(Directory.Exists(_root));
        Assert.Equal("somebody else's", File.ReadAllText(_root + "-quarantine"));
    }
}
