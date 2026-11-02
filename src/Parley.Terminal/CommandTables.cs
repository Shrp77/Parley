namespace Parley.Terminal;

/// <summary>Something that takes keys from a command table: the surface, or a dialog.</summary>
/// <typeparam name="TSelf">The type itself.</typeparam>
/// <remarks>
/// <para>
/// <b>The one way a table is declared</b>: each type builds its own here, once, and everything that needs it (the type's
/// key handler, a label naming a key, <see cref="CommandTables.BuildEvery"/>, the tests) asks for it by type. Before,
/// a table was three static properties found by their names, and a type that spelt one differently had no table any
/// check could see.
/// </para>
/// <para>
/// Static and abstract, so it is a fact about the type, readable before an instance exists: the tool builds every
/// table before it takes the console, when no dialog is open.
/// </para>
/// </remarks>
internal interface IKeyTable<TSelf>
    where TSelf : IKeyTable<TSelf>
{
    /// <summary>Builds and checks the type's table.</summary>
    /// <returns>The table.</returns>
    /// <exception cref="InvalidOperationException">The table refuses: a clash, an unreachable claim, or a plain key.</exception>
    static abstract CommandManager<TSelf> BuildTable();
}

/// <summary>Every command table the terminal has, built together so a clash in any of them is found before it starts.</summary>
/// <remarks>
/// <para>
/// <b>Built before the toolkit takes the console</b>, from <c>TerminalCommand.RunAsync</c>, so a refusal is a plain
/// message on standard error and a non-zero exit, never a stack trace over a screen the toolkit had already cleared.
/// The surface and each dialog build their own table again when they are made; by then this has proved they can.
/// </para>
/// <para>
/// <b>Every type that takes keys is listed here, once</b>, in <see cref="Every"/>. <c>KeysTakenBeforeTheTableTests</c>
/// finds every view the terminal can open as a session by reflection and fails on one not in <see cref="Targets"/>, so
/// a dialog added and left out is caught by a unit test rather than checked only when it opens.
/// </para>
/// </remarks>
internal static class CommandTables
{
    /// <summary>What the terminal exits with when a table refuses: a fault in this build, not in how it was run.</summary>
    internal const int Refusal = 70;

    /// <summary>Every type with a table, and how to build it.</summary>
    private static readonly IReadOnlyList<(Type Target, Func<IReadOnlyList<KeyPlace>> Build)> Every =
    [
        Listed<Shell>(),
        Listed<PlanView>(),
        Listed<ProposalView>(),
        Listed<TickDialog>(),
        Listed<ResponsibilityDialog>(),
        Listed<TextDialog>(),
        Listed<ChoiceDialog>(),
        Listed<FormDialog>(),
        Listed<Browsing>(),
        Listed<LogView>(),
        Listed<Health>(),
        Listed<Saying>(),
    ];

    /// <summary>Every type with a table, in the order they are built.</summary>
    internal static IReadOnlyList<Type> Targets => [.. Every.Select(listed => listed.Target)];

    /// <summary>Builds one type's table.</summary>
    /// <typeparam name="TTarget">The type.</typeparam>
    /// <returns>The table, checked.</returns>
    /// <remarks>Built afresh on every call, so a refusal is always an <see cref="InvalidOperationException"/> said plainly.</remarks>
    internal static CommandManager<TTarget> Of<TTarget>()
        where TTarget : IKeyTable<TTarget> =>
        TTarget.BuildTable();

    /// <summary>Builds every table, throwing the first refusal.</summary>
    /// <returns>The places every table declares, for a test to count.</returns>
    internal static IReadOnlyList<KeyContext> BuildEvery() =>
        [.. Every.SelectMany(listed => listed.Build()).Select(place => place.Context)];

    /// <summary>Builds every table, and says plainly why the terminal cannot start when one refuses.</summary>
    /// <param name="error">Where to say it.</param>
    /// <param name="build">What to build; every table unless a test says otherwise.</param>
    /// <returns><see langword="null"/> when every table was built, or the exit code.</returns>
    /// <remarks>
    /// The refusal names every clash and how to fix it, which is all a person can act on; a stack trace would bury that
    /// under frames of the toolkit's. Only <see cref="InvalidOperationException"/> is caught, which is what a table
    /// throws; anything else is a different fault and is left to be one.
    /// </remarks>
    internal static int? Refused(TextWriter error, Func<object>? build = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            _ = (build ?? BuildEvery)();

            return null;
        }
        catch (InvalidOperationException refused)
        {
            // A table's refusal leads with what kind it is, which is how this line ends.
            var lines = refused.Message.Split(Environment.NewLine, 2);

            error.WriteLine("parley-terminal cannot start: " + lines[0]);

            if (lines.Length > 1)
            {
                error.WriteLine(lines[1]);
            }

            return Refusal;
        }
    }

    /// <summary>One entry of <see cref="Every"/>.</summary>
    /// <typeparam name="TTarget">The type.</typeparam>
    /// <returns>The type, and how to build its table and read its places.</returns>
    private static (Type Target, Func<IReadOnlyList<KeyPlace>> Build) Listed<TTarget>()
        where TTarget : IKeyTable<TTarget> =>
        (typeof(TTarget), () => Of<TTarget>().Places);
}
