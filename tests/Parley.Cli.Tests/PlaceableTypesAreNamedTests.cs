using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// The command line's help and the Peers block enrolment writes name every kind of file that can be placed.
/// </summary>
/// <remarks>
/// Both are read by somebody deciding what to hand over: a person at <c>parley --help</c>, an agent reading its
/// repository's instructions. Held to <see cref="SharedFileName.AllowedExtensions"/>, so a type added there and left
/// out of either is caught: <c>.md</c> and <c>.txt</c> by name, an image by the word.
/// </remarks>
[Integration]
public sealed class PlaceableTypesAreNamedTests
{
    /// <summary>Fails unless the text names each allowed extension, or says "image" for an image's.</summary>
    private static void NamesEveryType(string text)
    {
        foreach (var extension in SharedFileName.AllowedExtensions)
        {
            var named = ImageSignature.IsImageName("x" + extension) ? "image" : extension;

            Assert.True(
                text.Contains(named, StringComparison.Ordinal),
                $"'{text}' does not name {extension} (looked for '{named}')");
        }
    }

    [Fact]
    public void The_help_for_placing_a_file_names_every_type_that_can_be_placed()
    {
        // Without the guard the help said a .md or .txt file could be placed, and a person with an image was told no
        // (review, lens 3 round 36).
        var (exitCode, output, _) = ToolProcess.RunFully([], "--help");

        Assert.Equal(0, exitCode);

        var placing = Assert.Single(
            output.ReplaceLineEndings("\n").Split('\n'),
            line => line.Contains("parley files place", StringComparison.Ordinal));

        NamesEveryType(placing);
    }

    [Fact]
    public void The_peers_block_names_every_type_that_can_be_placed()
    {
        // Without the guard every enrolled repository's instructions said only .md and .txt files could be placed, so
        // its agent never handed over an image (review, lens 3 round 36).
        var frame = EnrollmentPlan.Frame;
        var from = frame.IndexOf("Only ", StringComparison.Ordinal);

        Assert.True(from >= 0, "the Peers block no longer says what can be placed");

        var to = frame.IndexOf("placed", from, StringComparison.Ordinal);

        Assert.True(to > from, "the Peers block's sentence about placing does not end");

        NamesEveryType(frame[from..to]);
    }
}
