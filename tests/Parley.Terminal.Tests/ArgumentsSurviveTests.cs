using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the terminal reads its own command line, and refuses what it does not take.
/// </summary>
/// <remarks>
/// <para>
/// `parley terminal` forwards its arguments untouched, so this is the only parser and there is nothing above it
/// to correct a mistake — an argument misread here is misread for both spellings of the command.
/// </para>
/// <para>
/// <b>The first run of this found a real one.</b> An option given without a value returned null and
/// left the flag in the list, so `parley-terminal --config` was refused with "parley-terminal does not take
/// --config". That is false: it does take it, and what was missing was the path. A refusal that
/// misnames the problem sends somebody to check the spelling of an option they spelled correctly.
/// </para>
/// </remarks>
[Integration]
public sealed class ArgumentsSurviveTests
{
    [Theory]
    [InlineData("--config")]
    [InlineData("--broker")]
    [InlineData("-b")]
    public void An_option_with_no_value_says_the_value_is_missing(string option)
    {
        var (code, _, error) = TerminalProcess.Run(option);

        Assert.Equal(64, code);
        Assert.Contains("needs a value", error, StringComparison.Ordinal);

        // And specifically not the other refusal, which is the one this used to give.
        Assert.DoesNotContain("does not take", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_followed_by_another_option_is_a_missing_value_rather_than_a_path()
    {
        // `--config --broker x` must not read "--broker" as the manifest path. It would then look
        // for a file called --broker, fail to find one, and fall back to this machine's manifest --
        // showing a deployment nobody asked for while reporting nothing at all.
        var (code, _, error) = TerminalProcess.Run("--config", "--broker", "http://127.0.0.1:1");

        Assert.Equal(64, code);
        Assert.Contains("--config needs a value", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_it_does_not_have_is_refused_and_named()
    {
        var (code, _, error) = TerminalProcess.Run("--nonsense");

        Assert.Equal(64, code);
        Assert.Contains("--nonsense", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_argument_is_refused_rather_than_ignored()
    {
        // Ignoring it is the failure worth avoiding: somebody typing `parley-terminal north-helm` means
        // something by it, and a surface that opened on everything without a word would look like
        // it had honoured them.
        var (code, _, error) = TerminalProcess.Run("north-helm");

        Assert.Equal(64, code);
        Assert.Contains("north-helm", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_option_is_refused_because_the_surface_is_not_scoped_to_one_project()
    {
        var (code, _, error) = TerminalProcess.Run("--project", "north-helm");

        Assert.Equal(64, code);
        Assert.Contains("does not take --project", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_path_with_spaces_arrives_whole()
    {
        // The passthrough hazard. `Forwarding` hands arguments to the child through ArgumentList,
        // which quotes each one; a wrapper that joined them into a string would split this in two
        // and the terminal would look for a manifest named after the first word.
        var directory = Path.Combine(
            Path.GetTempPath(), $"parley probe {Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        // **A root with a project in it, rather than a manifest file.** What this is about is a path
        // containing spaces arriving as one argument, so what matters is that the path is real and is
        // read — and the root is now a directory, which cannot be written to with File.WriteAllText.
        var manifest = directory;

        ProjectManifest.Write(
            manifest,
            [new ProjectSettings(
                ProjectName.Create("alpha"),
                ProjectFolder.StoreFor(manifest, ProjectName.Create("alpha")),
                "http://127.0.0.1:1",
                null)]);

        try
        {
            var (_, _, error) = TerminalProcess.Run("--config", manifest);

            // It got far enough to refuse the redirected terminal, which is downstream of reading
            // the path: a path split in two would have failed earlier and differently.
            Assert.Contains("redirected", error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("does not take", error, StringComparison.Ordinal);
            Assert.DoesNotContain("needs a value", error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Asking_it_what_it_is_works_in_a_pipe()
    {
        // The two questions that must answer without a terminal, because the first thing somebody
        // does with a tool that will not start is ask what it is.
        var (version, said, _) = TerminalProcess.Run("--version");

        Assert.Equal(0, version);
        Assert.Contains("parley-terminal", said, StringComparison.Ordinal);
        Assert.Contains("protocol", said, StringComparison.Ordinal);

        var (help, manual, _) = TerminalProcess.Run("--help");

        Assert.Equal(0, help);
        Assert.Contains("--config", manual, StringComparison.Ordinal);
        Assert.Contains("`parley`", manual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_awkward_argument_survives_the_wrapper_as_one_argument()
    {
        // The passthrough hazard, through the real wrapper to the real terminal. A wrapper that
        // joined its arguments into a command line would split this path at the first space, and
        // the terminal would report a bare argument it was never given.
        var directory = Path.Combine(Path.GetTempPath(), $"parley wrapper {Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        // **A root with a project in it, rather than a manifest file.** What this is about is a path
        // containing spaces arriving as one argument, so what matters is that the path is real and is
        // read — and the root is now a directory, which cannot be written to with File.WriteAllText.
        var manifest = directory;

        ProjectManifest.Write(
            manifest,
            [new ProjectSettings(
                ProjectName.Create("alpha"),
                ProjectFolder.StoreFor(manifest, ProjectName.Create("alpha")),
                "http://127.0.0.1:1",
                null)]);

        var said = new StringWriter();

        try
        {
            var code = await Parley.Cli.Forwarding.RunAsync(
                TerminalProcess.Executable(),
                "Parley.Terminal",
                "parley terminal",
                ["--config", manifest],
                said);

            // 1 is the redirected-terminal refusal, which is downstream of reading the path. 64 is
            // what a split path would produce: a bare argument the terminal does not take.
            Assert.Equal(1, code);
            Assert.Equal(string.Empty, said.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
