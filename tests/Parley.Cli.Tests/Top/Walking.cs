using Parley.Cli.Top;
using Parley.Client.Views;
using Parley.Core;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// Putting a driven surface on one of the three views, and visiting every pane of every view.
/// </summary>
/// <remarks>
/// <para>
/// One copy, because 006 replaced "four regions, indexes 0 to 3" with "three views of one or two
/// panes each", and every test that had walked the four had to learn the new shape. Written down
/// once so the next change to the shape is one edit rather than eighteen.
/// </para>
/// <para>
/// <b>These arrive by the direct route on purpose, and one test does not.</b> Setting a place is
/// how a test reaches the project view without first building a deployment to navigate through;
/// but a suite that only ever arrived that way could not tell that the route a person takes had
/// stopped working, so <c>NavigatingTests</c> walks in by selecting and pressing Enter.
/// </para>
/// </remarks>
internal static class Walking
{
    /// <summary>A project name every fixture can use.</summary>
    internal static ProjectName Alpha { get; } = ProjectName.Create("alpha");

    /// <summary>Puts the surface on a view and waits for its reads.</summary>
    /// <param name="shell">The surface.</param>
    /// <param name="viewing">Which view.</param>
    /// <param name="conversation">The conversation, when going to that view.</param>
    /// <returns>When the view is on screen and settled.</returns>
    internal static async Task GoAsync(
        Shell shell, Viewing viewing, string conversation = "some-discussion")
    {
        var place = viewing switch
        {
            Viewing.Project => ViewPlace.Into(Alpha),
            Viewing.Conversation => ViewPlace.Into(Alpha).Into(conversation),
            _ => ViewPlace.Top,
        };

        await shell.GoAsync(place);
        await shell.Settled;
    }

    /// <summary>Every pane of every view, in the order a person would meet them.</summary>
    /// <param name="shell">The surface.</param>
    /// <returns>The panes, with the surface left on the last of them.</returns>
    /// <remarks>
    /// Yields rather than returning a list, so the caller assertions run while the surface is
    /// actually on that view. A list built first and asserted after would be asserting against
    /// whatever view the walk finished on.
    /// </remarks>
    internal static async IAsyncEnumerable<int> EveryPaneAsync(Shell shell)
    {
        foreach (var viewing in new[] { Viewing.Projects, Viewing.Project, Viewing.Conversation })
        {
            await GoAsync(shell, viewing);

            for (var pane = 0; pane < shell.Panes.Count; pane++)
            {
                shell.Focus(pane);

                yield return pane;
            }
        }
    }
}
