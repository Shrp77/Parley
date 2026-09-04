using Spectre.Console.Testing;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// Driving the surface from a test: a console that behaves as a terminal, and the two keys every
/// journey is made of.
/// </summary>
/// <remarks>
/// One copy, for the reason <c>Loopback</c> is one copy: three test files each carried these
/// three helpers within a day of one another. <see cref="Pick"/> chooses by name against the list
/// the surface builds, never by a counted number of arrow keys, so a menu that gains an entry
/// moves the highlight rather than silently sending a test somewhere else — which is also why
/// every menu here exposes the list it draws.
/// </remarks>
internal static class Driving
{
    /// <summary>A console that reports itself interactive and wide enough that nothing wraps.</summary>
    /// <returns>The console.</returns>
    public static TestConsole Interactive()
    {
        var console = new TestConsole();

        console.Interactive();
        console.Profile.Width = 200;

        return console;
    }

    /// <summary>Moves the highlight to a named choice and takes it.</summary>
    /// <param name="console">The console to push the keys onto.</param>
    /// <param name="choices">The list the surface will draw, in its order.</param>
    /// <param name="wanted">The entry to take.</param>
    public static void Pick(TestConsole console, IReadOnlyList<string> choices, string wanted)
    {
        var at = choices.ToList().IndexOf(wanted);

        Assert.True(at >= 0, $"'{wanted}' is not on the screen it was expected on");

        for (var step = 0; step < at; step++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        Enter(console);
    }

    /// <summary>Takes whatever is highlighted, or accepts a prompt's proposal.</summary>
    /// <param name="console">The console to push the key onto.</param>
    public static void Enter(TestConsole console) => console.Input.PushKey(ConsoleKey.Enter);
}
