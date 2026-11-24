using System.Text;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a project file and the settings file come back from a write with the line endings, byte order mark and unknown
/// members they went in with (CLAUDE.md, project files).
/// </summary>
/// <remarks>
/// Review, quality lens (0.63.2): a property check over 200 random files, because the serializer indents with the platform's
/// newline and the two fixed cases (all LF, all CRLF) would not have caught a file that is mixed or marked.
/// </remarks>
[Integration]
public sealed class AWrittenConfigurationFileKeepsItsShapeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-shape-" + Path.GetRandomFileName());

    public AWrittenConfigurationFileKeepsItsShapeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Body(Random random, string newline)
    {
        var members = new List<string>();

        for (var i = 0; i < random.Next(0, 5); i++)
        {
            members.Add($"  \"unknown{i}\": {{ \"n\": {random.Next(100)}, \"s\": \"café {i}\" }}");
        }

        return "{" + newline + string.Join("," + newline, members) + (members.Count > 0 ? newline : string.Empty) + "}"
            + (random.Next(2) == 0 ? newline : string.Empty);
    }

    private static void AssertShape(string path, bool crlf, bool mark, int unknown)
    {
        var bytes = File.ReadAllBytes(path);
        var hasMark = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, hasMark ? 3 : 0, bytes.Length - (hasMark ? 3 : 0));

        Assert.Equal(mark, hasMark);

        var bare = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);

        if (crlf)
        {
            Assert.DoesNotContain('\n', bare);
            Assert.DoesNotContain('\r', bare);
        }
        else
        {
            Assert.DoesNotContain('\r', text);
        }

        for (var i = 0; i < unknown; i++)
        {
            Assert.Contains($"\"unknown{i}\"", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Two_hundred_settings_files_keep_their_endings_mark_and_unknown_members()
    {
        var random = new Random(20261119);

        for (var round = 0; round < 200; round++)
        {
            var crlf = random.Next(2) == 0;
            var mark = random.Next(2) == 0;
            var body = Body(random, crlf ? "\r\n" : "\n");
            var unknown = body.Split("\"unknown", StringSplitOptions.None).Length - 1;
            var path = Path.Combine(_root, ParleySettings.FileName);

            File.WriteAllText(path, body, new UTF8Encoding(mark));
            Assert.Null(ParleySettings.WriteClosedDays(_root, 1 + (round % 300)));

            // A file with no member at all is written as one line break style by default (LF) when it had none to keep.
            AssertShape(path, crlf && body.Contains("\r\n", StringComparison.Ordinal), mark, unknown);
        }
    }

    [Fact]
    public void Two_hundred_project_files_keep_their_endings_mark_and_unknown_members()
    {
        var random = new Random(20261120);
        var name = ProjectName.Create("orders");

        for (var round = 0; round < 200; round++)
        {
            var crlf = random.Next(2) == 0;
            var mark = random.Next(2) == 0;
            var body = Body(random, crlf ? "\r\n" : "\n");
            var unknown = body.Split("\"unknown", StringSplitOptions.None).Length - 1;
            var path = ProjectFolder.ConfigurationFor(_root, name);

            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body, new UTF8Encoding(mark));
            ProjectFolder.Write(_root, new ProjectSettings(name, ProjectFolder.StoreFor(_root, name), "http://127.0.0.1:19100", null));

            AssertShape(path, crlf && body.Contains("\r\n", StringComparison.Ordinal), mark, unknown);
        }
    }
}
