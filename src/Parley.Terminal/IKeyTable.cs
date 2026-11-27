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
