using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// Naming a project from inside the surface, through the code the command runs.
/// </summary>
/// <remarks>
/// <para>
/// FR-015. Convenience rather than capability: <c>parley project add</c> has always done this, and
/// it is done once per project. What it buys is not having to leave a full-screen application to
/// run a command whose arguments are on the screen you left.
/// </para>
/// <para>
/// <b>It calls <see cref="ProjectCommand"/> rather than writing the manifest itself</b>, which is
/// the whole of the design. The manifest has exactly one writer, and this is that same program;
/// a second path that assembled a <c>ProjectSettings</c> and wrote it would be a second writer
/// with its own idea of the rules — and it would sidestep the lock that stops two concurrent
/// writers discarding one another's work, which was a real defect fixed in #78 and not a
/// hypothetical one.
/// </para>
/// </remarks>
internal static class Naming
{
    /// <summary>Collects what a project needs and names it.</summary>
    /// <param name="console">Where to draw, and where the fields are typed.</param>
    /// <param name="manifestPath">The manifest to write into.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>When it has been named, or refused.</returns>
    /// <remarks>
    /// The refusal comes back from the command's own validation and is shown as it was written.
    /// A surface that summarised it to "invalid" would take away the one thing that says which
    /// rule was broken.
    /// </remarks>
    internal static async Task AddAsync(
        IAnsiConsole console,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var name = console.Prompt(new TextPrompt<string>("Project name:"));
        var store = console.Prompt(new TextPrompt<string>("Database path:"));
        var listen = console.Prompt(new TextPrompt<string>("Broker address:"));

        var nodeConfig = console.Prompt(
            new TextPrompt<string>("Node catalogue [dim](optional)[/]:").AllowEmpty());

        var description = console.Prompt(
            new TextPrompt<string>("Description [dim](optional)[/]:").AllowEmpty());

        var said = new StringWriter();
        var complained = new StringWriter();

        var code = await ProjectCommand.RunAsync(
            ["add", name],
            manifestPath,
            new ProjectFields(
                store,
                listen,
                nodeConfig.Length == 0 ? null : nodeConfig,
                description.Length == 0 ? null : description),
            said,
            complained,
            cancellationToken).ConfigureAwait(false);

        Report(console, code, said.ToString(), complained.ToString());
    }

    /// <summary>Shows what the command said, in its own words.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="code">What the command returned.</param>
    /// <param name="said">What it wrote to output.</param>
    /// <param name="complained">What it wrote to error.</param>
    private static void Report(IAnsiConsole console, int code, string said, string complained)
    {
        foreach (var line in said.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            console.MarkupLine($"[dim]{ProjectList.Escape(line.TrimEnd())}[/]");
        }

        foreach (var line in complained.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            console.MarkupLine($"[red]{ProjectList.Escape(line.TrimEnd())}[/]");
        }

        console.MarkupLine(
            code == 0
                ? "[green]Named.[/] [dim]It will appear on the project list without restarting this.[/]"
                : "[yellow]Nothing was written.[/]");
    }
}
