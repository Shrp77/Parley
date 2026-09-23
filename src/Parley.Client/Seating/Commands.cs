namespace Parley.Client.Seating;

/// <summary>What a command's parameter is, and so how it is resolved.</summary>
public enum Takes
{
    /// <summary>An agent, resolved through the same completion the <c>@</c> sigil uses.</summary>
    Agent = 0,

    /// <summary>Text, taken as written.</summary>
    Text = 1
}

/// <summary>Whether a command changes what is about to be sent, or does something.</summary>
public enum Does
{
    /// <summary>Changes the draft. Not sendable on its own.</summary>
    Draft = 0,

    /// <summary>Performs an act.</summary>
    Acting = 1
}

/// <summary>One parameter a command takes.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Required">Whether the command can run without it.</param>
public sealed record Parameter(Takes Kind, bool Required);

/// <summary>
/// One command, declared rather than branched on.
/// </summary>
/// <param name="Id">What is typed after <c>/</c>. Contains no <c>/</c> of its own.</param>
/// <param name="Says">One line, shown in the completion list.</param>
/// <param name="Takes">Its parameters, in order.</param>
/// <param name="Kind">Whether it changes the draft or acts.</param>
/// <param name="Door">
/// What must be described and typed back before a destructive command runs, or <see langword="null"/> when it
/// destroys nothing.
/// </param>
/// <remarks>
/// <para>
/// <b>The input line parses, completes, refuses and lists from these.</b> A command added later is a row in
/// <see cref="Commands.Table"/> rather than another branch in the line — and if adding one requires touching the
/// line, this has been broken.
/// </para>
/// <para>
/// <b>A destructive command declares its door here</b>, so the door is part of what the command <em>is</em>
/// rather than something a handler remembers to open. That is what keeps the guarantee checkable as operations
/// move into commands: `SurfaceTests` proves today that each destructive operation's name appears in exactly one
/// file containing its prompt, and with a generic dispatch the name moves into a declaration — so the check must
/// read declarations or it will keep passing while meaning less.
/// </para>
/// </remarks>
public sealed record Command(string Id, string Says, IReadOnlyList<Parameter> Takes, Does Kind, string? Door = null);

/// <summary>What the line resolved a leading slash into.</summary>
/// <param name="Command">The command, or <see langword="null"/> when this is text.</param>
/// <param name="Rest">What followed the identifier.</param>
/// <param name="NearMiss">
/// A command the identifier is a prefix of but did not complete, or <see langword="null"/>. Set only when this is
/// text: it is what the line names so a typo is visible before Enter rather than in somebody's inbox.
/// </param>
public sealed record Resolved(Command? Command, string Rest, string? NearMiss = null);

/// <summary>
/// The commands, and what a leading slash means.
/// </summary>
/// <remarks>
/// <para>
/// <b>A command is <c>/</c> and an identifier with no second <c>/</c>.</b> Anything else beginning with a slash
/// is text, which decides <c>/etc/hosts</c> without reference to what any command is called — so the paths
/// somebody may ask about never have to be reconciled with the commands that exist.
/// </para>
/// <para>
/// <b>An unrecognised identifier is text, not a refusal.</b> The identifiers a person might type are not a closed
/// set they can be expected to know. What makes that survivable is that completing from the list is the ordinary
/// way to enter a command, plus the near miss this type reports: a typo is named before Enter rather than sent to
/// everybody as an ask they must answer.
/// </para>
/// </remarks>
public static class Commands
{
    /// <summary>Every command.</summary>
    public static IReadOnlyList<Command> Table { get; } =
    [
        new(
            "add",
            "add an agent that is not in this conversation yet",
            [new Parameter(Takes.Agent, Required: true)],
            Does.Draft),
    ];

    /// <summary>Every command's identifier, for the completion list.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. Table.Select(c => c.Id)];

    /// <summary>What a line beginning with a slash amounts to.</summary>
    /// <param name="text">The whole line, as typed.</param>
    /// <returns>The command and what followed it, or text with any near miss named.</returns>
    /// <remarks>
    /// An <em>exact</em> match of the identifier, which is deliberately not what the completion list does: the
    /// list narrows on prefix and is a suggestion, while this is the fact. <c>/ad</c> is text even while the list
    /// offers <c>add</c>, because nothing completed it.
    /// </remarks>
    public static Resolved Resolve(string? text)
    {
        var line = text ?? string.Empty;

        if (!line.StartsWith('/'))
        {
            return new Resolved(null, line);
        }

        var rest = line[1..];
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        var id = space < 0 ? rest : rest[..space];
        var after = space < 0 ? string.Empty : rest[(space + 1)..];

        // A second slash makes it a path, a route, or something else somebody is asking about.
        if (id.Contains('/', StringComparison.Ordinal))
        {
            return new Resolved(null, line);
        }

        if (Table.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) is { } found)
        {
            return new Resolved(found, after);
        }

        // Text, with the command it nearly was — which is only worth naming while it is still a prefix of one.
        var nearly = Table.FirstOrDefault(c =>
            id.Length > 0 && c.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase));

        return new Resolved(null, line, nearly?.Id);
    }

    /// <summary>Why a resolved command cannot run as written, or null when it can.</summary>
    /// <param name="command">The command.</param>
    /// <param name="given">How many parameters were supplied.</param>
    /// <returns>The refusal, or null.</returns>
    /// <remarks>
    /// Said rather than swallowed, and this is not the unrecognised case: the identifier resolved, so the person
    /// meant a command and is owed the reason it could not run.
    /// </remarks>
    public static string? Wrong(Command command, int given)
    {
        ArgumentNullException.ThrowIfNull(command);

        var required = command.Takes.Count(p => p.Required);

        if (given < required)
        {
            return $"/{command.Id} needs {required} thing{(required == 1 ? string.Empty : "s")}: "
                 + $"{command.Says}.";
        }

        return given > command.Takes.Count
            ? $"/{command.Id} takes {command.Takes.Count} and was given {given}."
            : null;
    }
}
