using Parley.Client;
using Parley.Core;

namespace Parley.Terminal;

/// <summary>Starting a new conversation from one the person is reading (SPEC.md §5.0, <c>spin_off</c>).</summary>
/// <remarks>
/// <c>SpinOffAsync</c> lives here and nowhere else in this surface, carrying its door. The surface asks for a name and opens the
/// new conversation with the message quoted in the box. Nothing is sent until the person presses Enter there, and the link
/// is recorded in that same act, just before the send, so a Request can never arrive with no link behind it.
/// </remarks>
internal static class SpinningOff
{
    /// <summary>The action's name, on the status line and in the menu.</summary>
    internal const string Title = "Spin off a conversation from this message";

    /// <summary>What the person is told when asked for the name.</summary>
    internal const string Door =
        "A new conversation, linked to this one. Nothing is sent until you press Enter in the box.";

    /// <summary>Why a name cannot be spun off to, told before the person writes anything rather than at the send.</summary>
    /// <param name="name">The name asked for.</param>
    /// <param name="parent">The conversation it comes from.</param>
    /// <param name="there">What the broker holds under the name.</param>
    /// <returns>A sentence, or <see langword="null"/> when the name looks free.</returns>
    /// <remarks>
    /// What the export can show of the broker's own refusal (<c>spin_off</c>), which still decides at the send. A stub
    /// already linked to this parent is free: it is what a failed attempt to take a link back leaves, and the broker takes it.
    /// </remarks>
    internal static string? NameProblem(string name, string parent, ConversationExport there)
    {
        ArgumentNullException.ThrowIfNull(there);

        if (name.StartsWith(Facilitation.ConversationPrefix, StringComparison.Ordinal))
        {
            return $"a name beginning {Facilitation.ConversationPrefix} belongs to the facilitator's conversations.";
        }

        if (there.Item is not null)
        {
            return $"the name {name} is reserved for a roadmap item.";
        }

        var held = string.Equals(name, parent, StringComparison.Ordinal)
            || there.Destroyed is not null
            || there.Exchanges.Count > 0
            || there.Messages.Count > 0
            || there.Moves is { Count: > 0 }
            || there.Links is { Children.Count: > 0 }
            || (there.Links?.Parent is { } other && !string.Equals(other, parent, StringComparison.Ordinal));

        return held ? $"there is already a conversation called {name}." : null;
    }

    /// <summary>Records the link, just before the first Request is sent.</summary>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="parent">The conversation it comes from.</param>
    /// <param name="child">The new conversation.</param>
    /// <param name="token">Cancels the call.</param>
    /// <returns>Why nothing should be sent, or <see langword="null"/> when the link is recorded.</returns>
    internal static Task<string?> LinkAsync(BrokerClient client, string parent, string child, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);

        return client.SpinOffAsync(parent, child, token);
    }

    /// <summary>Takes the link back when its first Request reached nobody, so no record says it was started.</summary>
    /// <param name="client">The operator's client for this project.</param>
    /// <param name="parent">The conversation it came from.</param>
    /// <param name="child">The conversation that was to be started.</param>
    /// <param name="token">Cancels the call.</param>
    /// <returns>When it has been asked.</returns>
    internal static Task AbandonAsync(BrokerClient client, string parent, string child, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);

        return client.AbandonSpinOffAsync(parent, child, token);
    }
}
